using System;
using UnityEngine;

namespace A2BKit.Core
{
    /// <summary>
    /// Marks a <c>[SerializeReference]</c> field so the inspector offers a type picker for it.
    ///
    /// This exists because Unity 6 still ships NO built-in picker for managed references: an
    /// interface-typed <c>[SerializeReference]</c> field renders as a bare, permanently-null row with
    /// no way to choose an implementation. Without this attribute the entire polymorphic config
    /// story (FR-10, FR-19) is unreachable from the inspector — a designer literally cannot assign a
    /// path — so the attribute plus its drawer are load-bearing, not decoration.
    ///
    /// It lives in A2BKit.Core rather than A2BKit.Unity for a hard compile reason: the fields it must
    /// annotate (<see cref="A2BEffectDefinition"/>'s Path/Easing/Emission) are Core types, and Core
    /// references nothing but UniTask. An attribute in A2BKit.Unity could not be applied to them —
    /// the reference runs the wrong way. AD-1 bans scene-graph types from Core, not UnityEngine value
    /// surface; PropertyAttribute is the same category as the [Tooltip]/[Min]/[Range] this assembly
    /// already uses, so nothing about AD-1 relaxes here. A2BKit.Unity references Core, so
    /// A2BEffectAsset.Payload can use it too.
    ///
    /// The drawer lives in A2BKit.Editor and is the only consumer; at runtime this attribute is inert.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class A2BSubclassSelectorAttribute : PropertyAttribute
    {
        /// <summary>
        /// Whether the picker offers "None". A definition whose Path is null fails
        /// <see cref="A2BEffectDefinition.Validate"/>, so null is a legitimate — and inspectable —
        /// authoring state rather than something to hide (AD-8: surface it, never throw on it).
        /// </summary>
        public bool AllowNull { get; }

        public A2BSubclassSelectorAttribute(bool allowNull = true)
        {
            AllowNull = allowNull;
        }
    }
}
