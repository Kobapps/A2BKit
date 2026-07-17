using System;
using System.Collections.Generic;
using System.Reflection;
using A2BKit.Core;
using UnityEditor;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// The type picker Unity does not ship (FR-10, FR-19).
    ///
    /// A <c>[SerializeReference]</c> interface field renders, out of the box in Unity 6, as a row with
    /// no control on it. The reference can only ever be set from code, which silently demotes the
    /// package's whole open/closed config story to a programmer feature and leaves designers staring
    /// at a null Path they cannot fix. This drawer is therefore required work, not polish.
    ///
    /// Implementations are enumerated with <see cref="TypeCache"/> rather than by scanning
    /// AppDomain assemblies: TypeCache is served from the editor's prebuilt index, so a user's own
    /// path type in their own assembly appears with no registration step (FR-10) and no per-repaint
    /// reflection cost.
    /// </summary>
    [CustomPropertyDrawer(typeof(A2BSubclassSelectorAttribute))]
    public sealed class A2BSerializeReferenceDrawer : PropertyDrawer
    {
        private const string NullDisplayName = "None";

        /// <summary>
        /// One candidate implementation, with its display label built once.
        ///
        /// Cached per declared interface type and never invalidated by hand: every field here is
        /// static, and a domain reload — the only event that can change the answer, because it is the
        /// only way new types enter the editor — wipes statics for us.
        /// </summary>
        private readonly struct Candidate
        {
            public readonly Type Type;
            public readonly GUIContent Label;

            public Candidate(Type type, GUIContent label)
            {
                Type = type;
                Label = label;
            }
        }

        /// <summary>Cache key: the same interface can be picked with and without a "None" entry.</summary>
        private readonly struct CacheKey : IEquatable<CacheKey>
        {
            private readonly Type _declaredType;
            private readonly bool _allowNull;

            public CacheKey(Type declaredType, bool allowNull)
            {
                _declaredType = declaredType;
                _allowNull = allowNull;
            }

            public bool Equals(CacheKey other) => _declaredType == other._declaredType && _allowNull == other._allowNull;
            public override bool Equals(object obj) => obj is CacheKey other && Equals(other);
            public override int GetHashCode() => (_declaredType?.GetHashCode() ?? 0) ^ (_allowNull ? 1 : 0);
        }

        private static readonly Dictionary<CacheKey, Candidate[]> CandidateCache = new Dictionary<CacheKey, Candidate[]>();
        private static readonly Dictionary<CacheKey, GUIContent[]> LabelCache = new Dictionary<CacheKey, GUIContent[]>();

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;

            if (property.propertyType != SerializedPropertyType.ManagedReference)
                return height;

            if (!property.isExpanded || property.managedReferenceValue == null)
                return height;

            // Children are measured with the same walk that draws them, so the reserved rect can
            // never disagree with what lands in it — a mismatch here is the classic "drawer overlaps
            // the next field" bug.
            foreach (SerializedProperty child in EnumerateChildren(property))
                height += EditorGUI.GetPropertyHeight(child, true) + EditorGUIUtility.standardVerticalSpacing;

            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.ManagedReference)
            {
                // Misuse is reported in the inspector rather than thrown: an authoring mistake must
                // not take the whole inspector down with an exception (AD-8).
                EditorGUI.LabelField(position, label, new GUIContent("[A2BSubclassSelector] requires [SerializeReference]."));
                return;
            }

            EditorGUI.BeginProperty(position, label, property);

            bool allowNull = (attribute as A2BSubclassSelectorAttribute)?.AllowNull ?? true;
            Type declaredType = ResolveDeclaredType();
            Candidate[] candidates = GetCandidates(declaredType, allowNull);
            GUIContent[] labels = GetLabels(declaredType, allowNull);

            var headerRect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            float labelWidth = EditorGUIUtility.labelWidth;
            var foldoutRect = new Rect(headerRect.x, headerRect.y, labelWidth, headerRect.height);
            var popupRect = new Rect(
                headerRect.x + labelWidth,
                headerRect.y,
                Mathf.Max(24f, headerRect.width - labelWidth),
                headerRect.height);

            object current = property.managedReferenceValue;

            // Only a populated reference has children worth folding out to.
            if (current != null)
                property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, label, true);
            else
                EditorGUI.LabelField(foldoutRect, label);

            int currentIndex = IndexOf(candidates, current?.GetType());

            // A synchronous Popup, not a GenericMenu: a menu callback fires after OnGUI returns, by
            // which time the SerializedProperty it captured may address a different object (the
            // inspector rebuilds on selection change). Assigning a managed reference through a stale
            // property is silent corruption.
            int newIndex = EditorGUI.Popup(popupRect, currentIndex, labels);

            if (newIndex != currentIndex && newIndex >= 0 && newIndex < candidates.Length)
                Assign(property, candidates[newIndex].Type);

            if (property.isExpanded && property.managedReferenceValue != null)
            {
                float y = headerRect.yMax + EditorGUIUtility.standardVerticalSpacing;
                EditorGUI.indentLevel++;
                foreach (SerializedProperty child in EnumerateChildren(property))
                {
                    float childHeight = EditorGUI.GetPropertyHeight(child, true);
                    EditorGUI.PropertyField(new Rect(position.x, y, position.width, childHeight), child, true);
                    y += childHeight + EditorGUIUtility.standardVerticalSpacing;
                }
                EditorGUI.indentLevel--;
            }

            EditorGUI.EndProperty();
        }

        /// <summary>
        /// Instantiates the chosen type and stores it on the property.
        ///
        /// ApplyModifiedProperties runs here rather than being left to the enclosing editor because
        /// this drawer is also reachable from Unity's default inspector, which applies only when it
        /// notices a change — and a managed reference swap made behind its back is not always noticed.
        /// </summary>
        private static void Assign(SerializedProperty property, Type type)
        {
            object instance = null;
            if (type != null)
            {
                try
                {
                    instance = Activator.CreateInstance(type);
                }
                catch (Exception e)
                {
                    // A ctor that throws is the user's type misbehaving; report it against the asset
                    // being edited and leave the field as it was, rather than tearing down the
                    // inspector (AD-8, FR-23).
                    A2BLog.Error(property.serializedObject.targetObject,
                        "Could not create '" + type.FullName + "': " + e.Message);
                    return;
                }
            }

            property.managedReferenceValue = instance;
            if (instance != null)
                property.isExpanded = true;
            property.serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// The interface the field is declared as — the root of the picker's candidate set.
        /// Unwraps arrays and List&lt;T&gt; so a collection of references picks per element.
        /// </summary>
        private Type ResolveDeclaredType()
        {
            FieldInfo field = fieldInfo;
            if (field == null) return typeof(object);

            Type type = field.FieldType;
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return type.GetGenericArguments()[0];
            return type;
        }

        private static Candidate[] GetCandidates(Type declaredType, bool allowNull)
        {
            var key = new CacheKey(declaredType, allowNull);
            if (CandidateCache.TryGetValue(key, out Candidate[] cached))
                return cached;

            var list = new List<Candidate>(8);
            if (allowNull)
                list.Add(new Candidate(null, new GUIContent(NullDisplayName)));

            foreach (Type type in TypeCache.GetTypesDerivedFrom(declaredType))
            {
                if (!IsSelectable(type)) continue;
                list.Add(new Candidate(type, new GUIContent(BuildDisplayName(type), type.FullName)));
            }

            Candidate[] result = list.ToArray();
            CandidateCache[key] = result;
            return result;
        }

        private static GUIContent[] GetLabels(Type declaredType, bool allowNull)
        {
            var key = new CacheKey(declaredType, allowNull);
            if (LabelCache.TryGetValue(key, out GUIContent[] cached))
                return cached;

            Candidate[] candidates = GetCandidates(declaredType, allowNull);
            var labels = new GUIContent[candidates.Length];
            for (int i = 0; i < candidates.Length; i++)
                labels[i] = candidates[i].Label;

            LabelCache[key] = labels;
            return labels;
        }

        /// <summary>
        /// Filters the candidate set to types [SerializeReference] can actually store.
        ///
        /// Every clause matches a way Unity drops the value on the next reload rather than a matter
        /// of taste: value types box (and are refused outright), a type without [Serializable] does
        /// not round-trip, and an open generic or abstract has nothing to instantiate. Offering any
        /// of them produces a field that silently empties itself after a domain reload — the worst
        /// possible failure, because it looks like the user's fault.
        /// </summary>
        private static bool IsSelectable(Type type)
        {
            if (type == null) return false;
            if (!type.IsClass) return false;
            if (type.IsAbstract) return false;
            if (type.IsGenericTypeDefinition) return false;
            if (type.ContainsGenericParameters) return false;
            if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return false;
            if (!Attribute.IsDefined(type, typeof(SerializableAttribute))) return false;
            if (type.GetConstructor(Type.EmptyTypes) == null) return false;
            return true;
        }

        /// <summary>
        /// "A2BBezierPath" reads as "Bezier Path" in the dropdown: the A2B prefix is noise once the
        /// list is already scoped to one interface, and the full name is on the tooltip anyway.
        /// </summary>
        private static string BuildDisplayName(Type type)
        {
            string name = type.Name;
            if (name.StartsWith("A2B", StringComparison.Ordinal) && name.Length > 3)
                name = name.Substring(3);
            return ObjectNames.NicifyVariableName(name);
        }

        private static int IndexOf(Candidate[] candidates, Type type)
        {
            for (int i = 0; i < candidates.Length; i++)
                if (candidates[i].Type == type)
                    return i;
            return -1;
        }

        /// <summary>
        /// Walks the property's immediate children only. The end-property bound is what stops the
        /// walk from spilling into the next sibling field.
        /// </summary>
        private static IEnumerable<SerializedProperty> EnumerateChildren(SerializedProperty property)
        {
            SerializedProperty iterator = property.Copy();
            SerializedProperty end = iterator.GetEndProperty();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, end))
            {
                enterChildren = false;
                yield return iterator.Copy();
            }
        }
    }
}
