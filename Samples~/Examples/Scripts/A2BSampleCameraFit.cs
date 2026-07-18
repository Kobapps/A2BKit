using UnityEngine;

namespace A2BKit.Samples
{
    /// <summary>
    /// Keeps a set of objects framed no matter the Game View's aspect — so the 3D examples show in a
    /// portrait window as well as a landscape one, instead of the content falling outside a narrow
    /// vertical field of view. It only moves the camera along its own forward axis (distance), so the
    /// authored angle and height are preserved; it never rotates or repositions sideways.
    ///
    /// Cheap and self-correcting: it recomputes each LateUpdate from the current aspect and the framed
    /// objects' positions, so a resized Game View or a moved target reframes on the next frame.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class A2BSampleCameraFit : MonoBehaviour
    {
        [Tooltip("Objects to keep on screen. Their positions define the box the camera frames.")]
        public Transform[] Framed;

        [Tooltip("Extra breathing room around the framed objects. 1 = tight, 1.4 = comfortable.")]
        public float Padding = 1.4f;

        [Tooltip("World radius assumed around each framed point, so objects are not clipped at the edge.")]
        public float PointRadius = 0.8f;

        private Camera _camera;

        private void Awake() => _camera = GetComponent<Camera>();

        private void LateUpdate()
        {
            if (_camera == null || _camera.orthographic || Framed == null || Framed.Length == 0) return;

            // Centre and half-size of the framed points, expanded by PointRadius so nothing clips.
            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            int count = 0;
            for (int i = 0; i < Framed.Length; i++)
            {
                if (Framed[i] == null) continue;
                Vector3 p = Framed[i].position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                count++;
            }
            if (count == 0) return;

            Vector3 center = (min + max) * 0.5f;
            Vector3 extents = (max - min) * 0.5f + Vector3.one * Mathf.Max(0f, PointRadius);

            float halfV = _camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float tanV = Mathf.Tan(halfV);
            float aspect = _camera.aspect;

            // Distance needed so the box fits vertically AND horizontally (horizontal is the tight one in
            // portrait). Add the box's own depth so the near objects are not inside the camera.
            float distV = extents.y / Mathf.Max(1e-4f, tanV);
            float distH = extents.x / Mathf.Max(1e-4f, tanV * aspect);
            float distance = Mathf.Max(distV, distH) * Mathf.Max(1f, Padding) + extents.z;

            transform.position = center - transform.forward * distance;
        }
    }
}
