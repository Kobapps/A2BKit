using UnityEngine;
using UnityEngine.UI;

namespace A2BKit.Demo
{
    /// <summary>
    /// Punches a Transform's scale. Wire <c>OnItemArrivedEvent</c> to <see cref="Punch"/> so the wallet
    /// kicks each time a coin lands.
    ///
    /// The kick is what makes the arrival feel like it hit something. Note the target wants a CENTRE
    /// pivot — scaling about an edge pivot shoves the icon sideways instead of pulsing in place.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/Punch Scale")]
    public sealed class A2BDemoPunch : MonoBehaviour
    {
        [Tooltip("Transform to punch. Defaults to this one.")]
        public Transform Target;

        [Tooltip("Extra scale at full punch. 0.3 = 130%.")]
        public float Amount = 0.3f;

        [Tooltip("How fast the punch decays back to rest.")]
        [Min(0.1f)] public float Decay = 4.5f;

        private float _impulse;
        private Vector3 _rest = Vector3.one;

        private void Awake()
        {
            if (Target == null) Target = transform;
            _rest = Target.localScale;
        }

        /// <summary>Wire to OnItemArrivedEvent.</summary>
        public void Punch() => _impulse = 1f;

        private void Update()
        {
            if (_impulse <= 0f) return;
            _impulse = Mathf.Max(0f, _impulse - Time.deltaTime * Decay);
            Target.localScale = _rest * (1f + Amount * _impulse);
        }
    }

    /// <summary>
    /// Slides a Transform back and forth.
    ///
    /// This is the entire implementation of the moving-target sample. Nothing else is needed, and that
    /// is the demonstration: endpoints resolve every frame, so items track a target that moves *while
    /// they are already in flight*. Cache the destination at play time — the obvious optimisation — and
    /// the coins land where the wallet used to be.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/Oscillate")]
    public sealed class A2BDemoOscillate : MonoBehaviour
    {
        [Tooltip("Local-space travel from the starting position, at the extremes.")]
        public Vector3 Amplitude = new Vector3(260f, 0f, 0f);

        [Tooltip("Full back-and-forth cycles per second.")]
        [Min(0.01f)] public float Frequency = 0.35f;

        private Vector3 _origin;

        private void Awake() => _origin = transform.localPosition;

        private void Update()
            => transform.localPosition = _origin + Amplitude * Mathf.Sin(Time.time * Frequency * Mathf.PI * 2f);
    }

    /// <summary>
    /// Orbits a Transform around its starting point, and optionally spins it.
    ///
    /// Used by the cross-space sample so the 3D chest keeps moving: the projection from world space to
    /// the canvas is live, and a stationary chest would leave you unable to tell.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/Orbit")]
    public sealed class A2BDemoOrbit : MonoBehaviour
    {
        public float Radius = 1.6f;

        [Min(0.01f)] public float Speed = 0.4f;

        [Tooltip("Degrees per second of self-spin. Zero to keep the object's rotation.")]
        public Vector3 Spin = new Vector3(0f, 45f, 0f);

        private Vector3 _origin;

        private void Awake() => _origin = transform.localPosition;

        private void Update()
        {
            float a = Time.time * Speed * Mathf.PI * 2f;
            transform.localPosition = _origin + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * Radius;
            if (Spin != Vector3.zero) transform.Rotate(Spin * Time.deltaTime, Space.Self);
        }
    }

    /// <summary>
    /// Fills an XP bar. Wire <c>OnItemArrivedEvent</c> to <see cref="AddXp"/>.
    ///
    /// Driven by arrivals rather than a parallel timer on purpose: a timer would desync from the orbs
    /// the moment anything staggered, stalled or got cancelled, and the bar would finish filling while
    /// orbs were visibly still in the air. The bar should only know what actually landed.
    /// </summary>
    [AddComponentMenu("A2BKit/Demo/XP Bar")]
    public sealed class A2BDemoXpBar : MonoBehaviour
    {
        [Tooltip("The filled portion. Uses Image.fillAmount, so set its Image Type to Filled.")]
        public Image Fill;

        [Tooltip("Fraction of the bar each arriving orb adds.")]
        [Range(0.001f, 0.5f)] public float PerItem = 0.03f;

        [Tooltip("Wrap back to empty when full, so the sample keeps being watchable on a loop.")]
        public bool WrapWhenFull = true;

        private float _value;

        private void Awake() => Apply();

        /// <summary>Wire to OnItemArrivedEvent.</summary>
        public void AddXp()
        {
            _value += PerItem;
            if (_value >= 1f) _value = WrapWhenFull ? 0f : 1f;
            Apply();
        }

        private void Apply()
        {
            if (Fill != null) Fill.fillAmount = _value;
        }
    }
}
