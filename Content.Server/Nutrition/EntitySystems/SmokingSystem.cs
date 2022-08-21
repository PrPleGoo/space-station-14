using System.Linq;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Chemistry.EntitySystems;
using Content.Server.Clothing.Components;
using Content.Server.Nutrition.Components;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Clothing.EntitySystems;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Smoking;
using Content.Shared.Temperature;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;

namespace Content.Server.Nutrition.EntitySystems
{
    public sealed partial class SmokingSystem : EntitySystem
    {
        [Dependency] private readonly ReactiveSystem _reactiveSystem = default!;
        [Dependency] private readonly SolutionContainerSystem _solutionContainerSystem = default!;
        [Dependency] private readonly BloodstreamSystem _bloodstreamSystem = default!;
        [Dependency] private readonly AtmosphereSystem _atmos = default!;
        [Dependency] private readonly TransformSystem _transformSystem = default!;
        [Dependency] private readonly InventorySystem _inventorySystem = default!;
        [Dependency] private readonly ClothingSystem _clothing = default!;
        [Dependency] private readonly SharedItemSystem _items = default!;
        [Dependency] private readonly SharedInteractionSystem _interactionSystem = default!;

        private const float UpdateTimer = 3f;

        private float _timer = 0f;

        /// <summary>
        ///     We keep a list of active smokables, because iterating all existing smokables would be dumb.
        /// </summary>
        private readonly HashSet<EntityUid> _active = new();

        public override void Initialize()
        {
            SubscribeLocalEvent<SmokableComponent, IsHotEvent>(OnSmokableIsHotEvent);
            SubscribeLocalEvent<SmokableComponent, ComponentShutdown>(OnSmokableShutdownEvent);

            InitializeCigars();
        }

        public void SetSmokableState(EntityUid uid, SmokableState state, SmokableComponent? smokable = null,
            AppearanceComponent? appearance = null, ClothingComponent? clothing = null)
        {
            if (!Resolve(uid, ref smokable, ref appearance, ref clothing))
                return;

            smokable.State = state;
            appearance.SetData(SmokingVisuals.Smoking, state);

            var newState = state switch
            {
                SmokableState.Lit => smokable.LitPrefix,
                SmokableState.Burnt => smokable.BurntPrefix,
                _ => smokable.UnlitPrefix
            };

            _clothing.SetEquippedPrefix(uid, newState, clothing);
            _items.SetHeldPrefix(uid, newState);

            if (state == SmokableState.Lit)
                _active.Add(uid);
            else
                _active.Remove(uid);
        }

        private void OnSmokableIsHotEvent(EntityUid uid, SmokableComponent component, IsHotEvent args)
        {
            args.IsHot = component.State == SmokableState.Lit;
        }

        private void OnSmokableShutdownEvent(EntityUid uid, SmokableComponent component, ComponentShutdown args)
        {
            _active.Remove(uid);
        }

        public override void Update(float frameTime)
        {
            _timer += frameTime;

            if (_timer < UpdateTimer)
                return;

            // TODO Use an "active smoke" component instead, EntityQuery over that.
            foreach (var uid in _active.ToArray())
            {
                if (!TryComp(uid, out SmokableComponent? smokable))
                {
                    _active.Remove(uid);
                    continue;
                }

                if (!_solutionContainerSystem.TryGetSolution(uid, smokable.Solution, out var solution))
                {
                    _active.Remove(uid);
                    continue;
                }

                if (smokable.ExposeTemperature > 0 && smokable.ExposeVolume > 0)
                {
                    var transform = Transform(uid);

                    if (transform.GridUid is {} gridUid)
                    {
                        var position = _transformSystem.GetGridOrMapTilePosition(uid, transform);
                        _atmos.HotspotExpose(gridUid, position, smokable.ExposeTemperature, smokable.ExposeVolume, true);
                    }
                }

                var inhaledSolution = _solutionContainerSystem.SplitSolution(uid, solution, smokable.InhaleAmount * _timer);
                smokable.Owner.TryGetContainerMan(out var containerManager);

                if (solution.TotalVolume == FixedPoint2.Zero)
                {
                    RaiseLocalEvent(uid, new SmokableSolutionEmptyEvent(), true);
                    if(smokable.State == SmokableState.Burnt &&
                        containerManager != null)
                    {
                        if (_inventorySystem.IsEntityInSlots(containerManager.Owner, smokable.Owner, "mask") &&
                            _inventorySystem.TryUnequip(containerManager.Owner, "mask"))
                        {
                            var target = Transform(containerManager.Owner);
                            _interactionSystem.DoDrop(containerManager.Owner, smokable.Owner, target.Coordinates, true);
                        }
                    }
                }

                if (inhaledSolution.TotalVolume == FixedPoint2.Zero)
                    continue;

                // This is still awful. I hate this so much.
                // TODO: Please, someone refactor containers and free me from this bullshit.
                if (containerManager == null ||
                    !_inventorySystem.IsEntityInSlots(containerManager.Owner, smokable.Owner, "mask") ||
                    !TryComp(containerManager.Owner, out BloodstreamComponent? bloodstream))
                {
                    continue;
                }

                _reactiveSystem.ReactionEntity(containerManager.Owner, ReactionMethod.Ingestion, inhaledSolution);
                _bloodstreamSystem.TryAddToChemicals(containerManager.Owner, inhaledSolution, bloodstream);
            }

            _timer -= UpdateTimer;
        }
    }

    /// <summary>
    ///     Directed event raised when the smokable solution is empty.
    /// </summary>
    public sealed class SmokableSolutionEmptyEvent : EntityEventArgs
    {
    }
}
