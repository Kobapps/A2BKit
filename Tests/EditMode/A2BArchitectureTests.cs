using System;
using System.Collections.Generic;
using System.Reflection;
using A2BKit.Core;
using NUnit.Framework;

namespace A2BKit.Tests.EditMode
{
    /// <summary>
    /// Executable architecture rules.
    ///
    /// AD-1 claims its dependency direction is "compiler-enforced by assembly, not by convention".
    /// That is only half true, and the half that is false is the dangerous half: the asmdef genuinely
    /// prevents Core from touching UnityEngine.UI and TMPro (Core does not reference those assemblies),
    /// but Transform, GameObject, Camera and Canvas all live in UnityEngine.CoreModule, which is
    /// auto-referenced everywhere. Nothing stops someone typing `Transform` into Core and shipping it.
    ///
    /// That is exactly the leak AD-18 was written to prevent, and it would not fail a build — it would
    /// just quietly make Core untestable without a scene and dissolve the paradigm. So the rule gets a
    /// test. This class IS the enforcement AD-1 advertises.
    /// </summary>
    public sealed class A2BArchitectureTests
    {
        /// <summary>
        /// Scene-graph types. Core may use value math (Vector3, Quaternion, Color, AnimationCurve)
        /// but nothing that implies a live scene.
        /// </summary>
        private static readonly string[] BannedInCore =
        {
            "UnityEngine.Transform",
            "UnityEngine.RectTransform",
            "UnityEngine.GameObject",
            "UnityEngine.Component",
            "UnityEngine.MonoBehaviour",
            "UnityEngine.ScriptableObject",
            "UnityEngine.Camera",
            "UnityEngine.Canvas",
            "UnityEngine.RectTransformUtility",
        };

        private static Assembly CoreAssembly => typeof(A2BScheduler).Assembly;

        [Test]
        public void Core_assembly_does_not_reference_UI_or_TextMeshPro()
        {
            foreach (AssemblyName reference in CoreAssembly.GetReferencedAssemblies())
            {
                Assert.That(reference.Name, Does.Not.Contain("UnityEngine.UI"),
                    "A2BKit.Core must not reference uGUI (AD-1).");
                Assert.That(reference.Name, Does.Not.Contain("TextMeshPro"),
                    "A2BKit.Core must not reference TextMeshPro (AD-1).");
                Assert.That(reference.Name, Does.Not.Contain("A2BKit.Unity"),
                    "A2BKit.Core must not reference A2BKit.Unity — dependencies flow one way (AD-1).");
            }
        }

        [Test]
        public void Core_public_surface_exposes_no_scene_graph_types()
        {
            var violations = new List<string>();

            foreach (Type type in CoreAssembly.GetTypes())
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                         | BindingFlags.Instance | BindingFlags.Static
                                         | BindingFlags.DeclaredOnly;

                foreach (FieldInfo field in type.GetFields(flags))
                    Check(violations, type, field.Name, field.FieldType);

                foreach (PropertyInfo property in type.GetProperties(flags))
                    Check(violations, type, property.Name, property.PropertyType);

                foreach (MethodInfo method in type.GetMethods(flags))
                {
                    Check(violations, type, method.Name + "() return", method.ReturnType);
                    foreach (ParameterInfo parameter in method.GetParameters())
                        Check(violations, type, method.Name + "(" + parameter.Name + ")", parameter.ParameterType);
                }
            }

            Assert.That(violations, Is.Empty,
                "A2BKit.Core must not touch scene-graph types (AD-1). A port that needs a Transform " +
                "belongs in A2BKit.Unity behind IA2BPresenter (AD-18).\n" + string.Join("\n", violations));
        }

        [Test]
        public void Core_declares_no_MonoBehaviour_or_ScriptableObject()
        {
            foreach (Type type in CoreAssembly.GetTypes())
            {
                Assert.That(typeof(UnityEngine.MonoBehaviour).IsAssignableFrom(type), Is.False,
                    type.FullName + " is a MonoBehaviour in A2BKit.Core (AD-1).");
                Assert.That(typeof(UnityEngine.ScriptableObject).IsAssignableFrom(type), Is.False,
                    type.FullName + " is a ScriptableObject in A2BKit.Core (AD-1).");
            }
        }

        /// <summary>
        /// AD-2: anything stored behind an interface must be a class. A struct assigned to an
        /// interface-typed field boxes on assignment and again on every call — allocating in the hot
        /// loop while the code still reads as zero-alloc. This catches it at test time rather than
        /// leaving it to a mysterious FR-18 failure.
        /// </summary>
        [Test]
        public void Strategy_implementations_are_classes_never_structs()
        {
            Type[] ports =
            {
                typeof(IA2BPath), typeof(IA2BEasing), typeof(IA2BEmission),
                typeof(IA2BTimeSource), typeof(IA2BEndpointProvider), typeof(IA2BEffectListener)
            };

            foreach (Type port in ports)
            {
                foreach (Type type in CoreAssembly.GetTypes())
                {
                    if (type.IsInterface || type.IsAbstract) continue;
                    if (!port.IsAssignableFrom(type)) continue;

                    Assert.That(type.IsValueType, Is.False,
                        type.FullName + " implements " + port.Name + " as a struct. It will box on " +
                        "every call and silently break the allocation budget (AD-2).");
                }
            }
        }

        /// <summary>
        /// AD-13's endpoint invariant, asserted across every path the assembly ships — including any
        /// added later. A path that misses its destination makes Arrival (t &gt;= 1) meaningless, and
        /// FirstItemArrived is the hook this package exists to provide.
        /// </summary>
        [Test]
        public void Every_shipped_path_satisfies_the_endpoint_invariant()
        {
            var ctx = new A2BPathContext(
                new UnityEngine.Vector3(-3f, 1f, 2f),
                new UnityEngine.Vector3(5f, -2f, -1f),
                itemIndex: 3, itemCount: 12, seed: 0xC0FFEEu);

            foreach (Type type in CoreAssembly.GetTypes())
            {
                if (type.IsInterface || type.IsAbstract) continue;
                if (!typeof(IA2BPath).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                var path = (IA2BPath)Activator.CreateInstance(type);
                Assert.That(A2BPathConformance.SatisfiesEndpointInvariant(path, in ctx), Is.True,
                    type.FullName + " does not land on both endpoints (AD-13).");
            }
        }

        private static void Check(List<string> violations, Type owner, string member, Type used)
        {
            if (used == null) return;

            Type probe = used;
            if (probe.IsByRef || probe.IsArray || probe.IsPointer)
                probe = probe.GetElementType();
            if (probe == null) return;

            string name = probe.FullName;
            if (name == null) return;

            for (int i = 0; i < BannedInCore.Length; i++)
            {
                if (name == BannedInCore[i])
                    violations.Add(owner.FullName + "." + member + " uses " + name);
            }
        }
    }
}
