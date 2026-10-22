using System;
using System.Collections.Generic;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// A remapping policy decides, every frame, the two inputs of the time-remapping ODE:
    /// the injection rate u(t) and the recovery gain α(t) (see <see cref="RemapDynamics"/>).
    ///
    /// The built-in <see cref="FunctionPolicy"/> lets you type u(t) and α(t) as formulas, which covers most
    /// designs without code. Write a policy class when you need logic a formula can't express (custom
    /// sensors, networking, game state): subclass, declare knobs with <see cref="Range"/>/<see cref="Choice"/>/
    /// <see cref="Toggle"/> in the constructor (they appear in the HUD and web UI automatically), implement
    /// <see cref="Evaluate"/>, and register it with <see cref="PolicyCatalog.Register"/>.
    /// </summary>
    public abstract class RemapPolicy
    {
        /// <summary>Stable short id, used as the prefix of parameter ids and in presets.</summary>
        public abstract string Id { get; }
        public abstract string DisplayName { get; }
        /// <summary>One or two sentences shown in the web UI explaining what the policy does.</summary>
        public abstract string Description { get; }

        /// <summary>True if this policy needs the virtual <see cref="ZoneVolume"/> (water) to be shown and tracked.</summary>
        public virtual bool UsesZone => false;

        public readonly TunableSet Params = new TunableSet("Policy");

        /// <summary>Called when the policy becomes active. Clear any internal state here.</summary>
        public virtual void Reset() { }

        /// <summary>Returns u(t) and α(t) for this frame.</summary>
        public abstract RemapCommand Evaluate(in RemapContext ctx);

        // ── Helpers for declaring parameters (ids are namespaced as "<policy id>.<key>") ──

        protected Tunable Range(string key, string label, float min, float max, float def,
                                float step = 0.01f, string unit = "", string help = "")
            => Params.Add(Tunable.Range(Id + "." + key, label, min, max, def, step, unit, help));

        protected Tunable Choice(string key, string label, string[] options, int def = 0, string help = "")
            => Params.Add(Tunable.Choice(Id + "." + key, label, options, def, help));

        protected Tunable Toggle(string key, string label, bool def, string help = "")
            => Params.Add(Tunable.Toggle(Id + "." + key, label, def, help));
    }

    /// <summary>
    /// Registry of available policies. Factories (not instances) are stored so every <see cref="RemapChannel"/>
    /// can own its own policy instance with independent parameters and state.
    /// </summary>
    public static class PolicyCatalog
    {
        private static readonly List<Func<RemapPolicy>> Factories = new List<Func<RemapPolicy>>
        {
            () => new FunctionPolicy(),
        };

        /// <summary>Adds a custom policy. Call before the engine starts (e.g. from a BeforeSceneLoad method).</summary>
        public static void Register(Func<RemapPolicy> factory) => Factories.Add(factory);

        public static List<RemapPolicy> CreateAll()
        {
            var list = new List<RemapPolicy>(Factories.Count);
            foreach (Func<RemapPolicy> f in Factories) list.Add(f());
            return list;
        }

        public static RemapPolicy Create(string id)
        {
            foreach (Func<RemapPolicy> f in Factories)
            {
                RemapPolicy p = f();
                if (p.Id == id) return p;
            }
            Debug.LogWarning($"[RemappingTime] Unknown policy id '{id}', using the time function.");
            return new FunctionPolicy();
        }
    }
}
