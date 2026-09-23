using UnityEngine;

/// <summary>
/// Turns one transform at a constant rate, indefinitely, until something stops it.
///
/// Written for the Genie Wheel's background drift: the wheel turns slowly for the whole session,
/// and the feature's own result spin is a separate, far faster motion owned by GenieWheelView. The
/// two never blend into one another — the wheel stops dead before a result spin and is hidden
/// behind a transition while it happens — which is exactly why this can stay a dumb constant
/// rotator that knows nothing about slices, landings or the feature at all.
///
/// ONE WRITER AT A TIME. Whatever drives the result spin writes the same localRotation this does,
/// so it must call StopRotating() before touching the wheel. Left running, the two fight every
/// frame and the wheel jitters.
///
/// Nothing here is wheel-specific, so it suits any other decorative spin.
/// </summary>
public class ContinuousRotator : MonoBehaviour
{
    [Tooltip("Degrees per second around Z. SIGNED: negative turns clockwise on the canvas, positive anticlockwise. Direction lives in the sign rather than a separate toggle, so the two can never disagree with each other.")]
    [SerializeField] private float degreesPerSecond = -20f;

    [Tooltip("The transform to turn. Empty = this object's own. Point it at a child to spin that while the object holding this component stays still.")]
    [SerializeField] private Transform target;

    [Tooltip("Begin turning as the scene loads, with nothing needing to start it — right for a background element that is simply always moving. Unlike ImageAnimation's \"Start On Enable\", there is no trap here: this is a continuous decorative spin, not a one-shot that something else meant to time.")]
    [SerializeField] private bool rotateOnAwake = true;

    // Resolved once in Awake: the serialized target, or this object's own transform.
    private Transform rotatingTransform;

    // The angle this component believes it is at, kept rather than read back off the transform each
    // frame: localEulerAngles is derived from the quaternion, so round-tripping through it every
    // frame accumulates error over a spin that runs for a whole session. Re-synced by
    // StartRotating — see the note there, which is the part that matters.
    private float currentAngle;

    private bool isRotating;

    internal bool IsRotating => isRotating;

    private void Awake()
    {
        rotatingTransform = target != null ? target : transform;
        currentAngle = rotatingTransform.localEulerAngles.z;
        isRotating = rotateOnAwake;
    }

    private void Update()
    {
        if (!isRotating || rotatingTransform == null) return;

        currentAngle += degreesPerSecond * Time.deltaTime;

        // Wrapped every frame rather than left to grow. Unwrapped, a session-long spin reaches tens
        // of thousands of degrees, where float precision is coarse enough that the motion visibly
        // steps. The result can be negative, which Quaternion.Euler handles fine.
        currentAngle %= 360f;

        rotatingTransform.localRotation = Quaternion.Euler(0f, 0f, currentAngle);
    }

    /// <summary>
    /// Resumes the spin from wherever the transform is NOW, rather than from where this component
    /// last left it.
    ///
    /// That re-sync is the whole point of the method. While stopped, another owner is free to put
    /// the wheel anywhere — the result spin lands it on a slice, and a snap to a known start angle
    /// behind the transition would move it again. Without this, the first frame after resuming
    /// would jump the wheel back to this component's stale angle and undo them.
    /// </summary>
    internal void StartRotating()
    {
        if (rotatingTransform == null) return;

        currentAngle = rotatingTransform.localEulerAngles.z;
        isRotating = true;
    }

    /// <summary>
    /// Stops dead, with no wind-down — the wheel is meant to stop abruptly and then be covered.
    /// The transform is left exactly where it is, for the next owner to take over or read.
    /// </summary>
    internal void StopRotating()
    {
        isRotating = false;
    }
}
