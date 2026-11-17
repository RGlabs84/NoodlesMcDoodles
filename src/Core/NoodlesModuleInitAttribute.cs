using System;

namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// Marks a <c>static void</c>, no-argument method (any accessibility — everything lives in one assembly,
    /// per the project's "one DLL" rule, so <c>internal</c> is the normal choice) as a module's entry point.
    /// <see cref="NoodlesPlugin"/>
    /// finds every method carrying this attribute anywhere in the assembly via reflection and calls each one,
    /// in ascending <see cref="Order"/> (ties broken by declaring-type full name, for a stable, reproducible
    /// boot order across runs), each in its own try/catch — so modules never need a compile-time reference to
    /// each other, or to <see cref="NoodlesPlugin"/> itself, to be wired in. See `docs/impl/CORE-API.md`
    /// "Registering your module" for the exact pattern.
    ///
    /// Core's own modules use <c>order: 10</c> (after the hard-sequenced bootstrap in
    /// <see cref="NoodlesPlugin.Awake"/> — config binding, feature-flag scaffolding, the kill switch — which
    /// runs directly, not through this mechanism, since those have to exist before ANY module's <c>Init()</c>
    /// can call <see cref="Features.Register"/> or <see cref="PatchRegistry.Register"/>). Every other module
    /// should use the default (100) unless it has a specific reason to run earlier or later than a sibling
    /// module — Init() order only matters if one module's Init() reads state another module's Init() writes,
    /// which should be rare (Harmony patches themselves aren't APPLIED until <see cref="PatchRegistry.ApplyAll"/>,
    /// which always runs after every Init() regardless of order).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class NoodlesModuleInitAttribute : Attribute
    {
        public int Order { get; }

        public NoodlesModuleInitAttribute(int order = 100)
        {
            Order = order;
        }
    }
}
