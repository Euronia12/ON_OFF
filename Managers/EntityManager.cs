// =================================================================
// [스크립트 목적]  타입별 엔티티 레지스트리 통합. SpatialGrid 기반 공간 쿼리 제공
//                 적/아군/중립 탐색, 범위 쿼리, 최근접 탐색을 GC 없이 수행
// [주요 변수]      - _registries : 엔티티 타입 → EntityRegistry<T>
// [의존 관계]      - ManagerBase<EntityManager>, RegisterableEntity<T>, SpatialGrid<T>
// [InitOrder]      35
// [사용 예시]      EntityManager.Instance.Configure<Enemy>(cellSize: 8f, use2D: true);
//                  var near = EntityManager.Instance.FindNearestEnemy<Enemy>(pos, myTeam, 10f);
// =================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

public class EntityManager : ManagerBase<EntityManager>
{
    public override int InitOrder => 35;

    [Header("기본 그리드 설정")]
    [Tooltip("Configure 미호출 타입의 기본 셀 크기")]
    [SerializeField] private float _defaultCellSize = 10f;

    [Tooltip("기본 평면. true=XY(2D), false=XZ(3D 탑다운)")]
    [SerializeField] private bool _defaultUse2D = false;

    // 타입 → EntityRegistry<T>. object엔 참조 타입만 담기므로 박싱 없음 (타입 저장소 패턴)
    private readonly Dictionary<Type, object> _registries = new(8);

    protected override UniTask OnInitializeAsync(CancellationToken token)
        => UniTask.CompletedTask;

    protected override UniTask OnShutdownInternalAsync()
    {
        ClearAll();
        return UniTask.CompletedTask;
    }

    // ─────────────────────────────────────────────
    // 레지스트리 설정 / 조회
    // ─────────────────────────────────────────────

    /// <summary>타입별 그리드 파라미터 설정. 엔티티 등록 전에 호출 권장.</summary>
    public void Configure<T>(float cellSize, bool use2D) where T : class, IRegisterableEntity
    {
        var registry = GetOrCreateRegistry<T>();
        registry.Configure(cellSize, use2D);
    }

    private EntityRegistry<T> GetOrCreateRegistry<T>() where T : class, IRegisterableEntity
    {
        var type = typeof(T);
        if (!_registries.TryGetValue(type, out var raw))
        {
            raw = new EntityRegistry<T>(_defaultCellSize, _defaultUse2D);
            _registries[type] = raw;
        }
        return (EntityRegistry<T>)raw;
    }

    public EntityRegistry<T> Get<T>() where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>();

    // ─────────────────────────────────────────────
    // 등록 / 해제 / 위치 갱신 (RegisterableEntity가 호출)
    // ─────────────────────────────────────────────

    public void Register<T>(T entity) where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().Register(entity);

    public void Unregister<T>(T entity) where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().Unregister(entity);

    public void UpdatePosition<T>(T entity) where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().UpdatePosition(entity);

    // ─────────────────────────────────────────────
    // 쿼리 (GC Zero — 호출부가 List 재사용)
    // ─────────────────────────────────────────────

    public void QueryRadius<T>(Vector3 center, float radius, List<T> results, Predicate<T> filter = null)
        where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().QueryRadius(center, radius, results, filter);

    public T FindNearest<T>(Vector3 center, float maxRadius, Predicate<T> filter = null)
        where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().FindNearest(center, maxRadius, filter);

    /// <summary>적 팀(myTeam과 다른 팀) 중 최근접 탐색.</summary>
    public T FindNearestEnemy<T>(Vector3 center, int myTeam, float maxRadius)
        where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().FindNearest(center, maxRadius, e => e.TeamId != myTeam);

    /// <summary>아군 팀(myTeam과 같은 팀) 중 최근접 탐색.</summary>
    public T FindNearestAlly<T>(Vector3 center, int myTeam, float maxRadius)
        where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().FindNearest(center, maxRadius, e => e.TeamId == myTeam);

    public void QueryEnemies<T>(Vector3 center, float radius, int myTeam, List<T> results)
        where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().QueryRadius(center, radius, results, e => e.TeamId != myTeam);

    public void QueryByTeam<T>(Vector3 center, float radius, int team, List<T> results)
        where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().QueryRadius(center, radius, results, e => e.TeamId == team);

    /// <summary>등록된 모든 엔티티 반환 (순회용). 새 List 할당되므로 빈번 호출 주의.</summary>
    public List<T> FindAll<T>() where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().GetAll();

    public int GetCount<T>() where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().Count;

    public void ClearAll()
    {
        foreach (var raw in _registries.Values)
        {
            if (raw is IEntityRegistry registry)
                registry.Clear();
        }
    }

    public void Clear<T>() where T : class, IRegisterableEntity
        => GetOrCreateRegistry<T>().Clear();
}

// ─────────────────────────────────────────────
// 내부 레지스트리 (타입별 SpatialGrid 보유)
// ─────────────────────────────────────────────

/// <summary>박싱 없이 Clear 호출용 인터페이스</summary>
internal interface IEntityRegistry
{
    void Clear();
}

public class EntityRegistry<T> : IEntityRegistry where T : class, IRegisterableEntity
{
    private SpatialGrid<T> _grid;
    private readonly HashSet<T> _all = new(256);

    public int Count => _all.Count;

    public EntityRegistry(float cellSize, bool use2D)
    {
        _grid = new SpatialGrid<T>(cellSize, use2D);
    }

    /// <summary>그리드 재구성. 기존 엔티티 모두 새 그리드에 재등록.</summary>
    public void Configure(float cellSize, bool use2D)
    {
        var newGrid = new SpatialGrid<T>(cellSize, use2D);
        foreach (var e in _all)
            newGrid.Add(e);
        _grid = newGrid;
    }

    public void Register(T entity)
    {
        if (entity == null) return;
        if (_all.Add(entity))
            _grid.Add(entity);
    }

    public void Unregister(T entity)
    {
        if (entity == null) return;
        if (_all.Remove(entity))
            _grid.Remove(entity);
    }

    public void UpdatePosition(T entity)
    {
        if (entity == null) return;
        _grid.Update(entity);
    }

    public void QueryRadius(Vector3 center, float radius, List<T> results, Predicate<T> filter = null)
        => _grid.QueryRadius(center, radius, results, filter);

    public T FindNearest(Vector3 center, float maxRadius, Predicate<T> filter = null)
        => _grid.FindNearest(center, maxRadius, filter);

    /// <summary>전체 엔티티 새 List로 반환.</summary>
    public List<T> GetAll() => new List<T>(_all);

    public void Clear()
    {
        _all.Clear();
        _grid.Clear();
    }
}
