using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

/// <summary>
/// The open/close choreography shared by feature panels whose art is a TWO_PHASE ImageAnimation
/// (an opening, then a loop): scale up from nothing with the clip started a few frames early, cue
/// work off an exact frame of the opening, and close by playing the opening backwards while the
/// panel scales down. Used by the congratulations panel (FreeGameView) and the Winner panel
/// (GenieWheelView).
///
/// A plain class, one per panel, created in the owning view's Awake — that is when the panel's
/// scene scale and the clip's own setup are read, before anything has moved them. The owning view
/// keeps the sequencing (what happens between these steps), the GameObject activation, and anything
/// on the panel's children; this owns the panel's scale tween and borrows the clip, always putting
/// it back in Reset.
///
/// Never edits ImageAnimation (protected) — it only sets that component's public settings.
/// </summary>
public class PanelClipPlayback
{
    private readonly Transform panel;
    private readonly ImageAnimation clip;
    private readonly Vector3 homeScale;

    // The clip's own setup, captured at construction. The close swaps in a reversed frame list and
    // single-phase playback; Reset puts these back so the next open plays normally.
    private readonly List<Sprite> clipFrames;
    private readonly ImageAnimation.AnimationMode clipMode;
    private readonly float clipSpeed;
    private readonly bool clipLoops;

    private Tween scaleTween;

    /// <param name="panel">The transform that scales. Null = nothing scales; the clip still plays.</param>
    /// <param name="clip">The panel's TWO_PHASE clip. Null = the panel scales with no animation.</param>
    internal PanelClipPlayback(Transform panel, ImageAnimation clip)
    {
        this.panel = panel;
        this.clip = clip;
        homeScale = panel != null ? panel.localScale : Vector3.one;

        if (clip != null)
        {
            clipFrames = clip.textureArray;
            clipMode = clip.animationMode;
            clipSpeed = clip.AnimationSpeed;
            clipLoops = clip.doLoopAnimation;
        }
    }

    /// <summary>
    /// The opening: the panel scales up from 0 to its scene scale with a slight overshoot, holding the
    /// clip's first frame, and the clip is started earlyStartFrames before the scale-up ends.
    ///
    /// Started early because ImageAnimation draws a two-phase clip's first frame twice before moving
    /// on, so a clip started at full size sits still for about two frames. Counted in the clip's own
    /// frames, so it holds whatever the clip's speed. The owning view activates the panel first.
    /// </summary>
    internal IEnumerator ScaleUpAndPlay(float duration, int earlyStartFrames)
    {
        Reset();
        if (clip != null) clip.RevertToInitialState();

        if (panel == null || duration <= 0f)
        {
            StartClip();
            yield break;
        }

        panel.localScale = Vector3.zero;
        scaleTween = panel.DOScale(homeScale, duration).SetEase(Ease.OutBack);

        float clipStartAt = Mathf.Max(0f, duration - Mathf.Max(0, earlyStartFrames) * FrameTime());
        if (clipStartAt > 0f) yield return new WaitForSeconds(clipStartAt);
        StartClip();

        if (scaleTween.IsActive()) yield return scaleTween.WaitForCompletion();
        scaleTween = null;
        panel.localScale = homeScale;
    }

    /// <summary>
    /// Waits until the clip SHOWS the frame leadFrames before its opening ends. Read off the sprite on
    /// screen rather than timed: the clip's frames wait for rendered frames and run long, which would
    /// throw a timed cue several frames out. Reached-or-passed, so a skipped frame can't miss it. With
    /// no usable clip it returns at once, and a clip that never gets there is given up on after its
    /// computed opening length plus timeoutMargin.
    /// </summary>
    internal IEnumerator WaitForOpeningCue(int leadFrames, float timeoutMargin, string ownerTag)
    {
        if (!HasOpeningPhase() || clip.rendererDelegate == null) yield break;

        int openingFrames = Mathf.Clamp(clip.phase2StartIndex, 0, clip.textureArray.Count);
        int cueFrame = Mathf.Max(0, openingFrames - Mathf.Max(0, leadFrames));
        float timeout = OpeningDuration() + timeoutMargin;

        for (float elapsed = 0f; elapsed < timeout; elapsed += Time.deltaTime)
        {
            if (clip.textureArray.IndexOf(clip.rendererDelegate.sprite) >= cueFrame) yield break;
            yield return null;
        }

        Debug.LogWarning($"[{ownerTag}] The panel clip never reached its cue frame — carrying on anyway.");
    }

    /// <summary>
    /// The close: the clip's opening plays backwards once — holding its last frame — while the panel
    /// scales down to 0, linear so it visibly shrinks from the first reversed frame. Returns the
    /// scale-down tween to wait on, or null when nothing scales (no panel, or a duration of 0).
    /// </summary>
    internal Tween ReverseAndScaleDown(float duration)
    {
        PlayOpeningReversed();

        if (panel == null || duration <= 0f) return null;

        KillScaleTween();
        scaleTween = panel.DOScale(0f, duration).SetEase(Ease.Linear);
        return scaleTween;
    }

    /// <summary>
    /// Stops the clip. ImageAnimation drives itself with Invoke, so deactivating its object is not a
    /// reliable way to end it.
    /// </summary>
    internal void StopClip()
    {
        if (clip != null) clip.StopAnimation();
    }

    /// <summary>
    /// Everything back as the scene has it — the panel's scale and the clip's own frames, mode and
    /// speed — so the panel never reopens mid-scale or mid-close. Safe to call at any point.
    /// </summary>
    internal void Reset()
    {
        KillScaleTween();
        RestoreClip();
        if (panel != null) panel.localScale = homeScale;
    }

    // Plays the opening backwards on the same component: the opening frames reversed, single-phase
    // with looping off, so it stops and holds on the opening's first frame. Same per-frame pace as
    // the opening — a single-phase clip's frame time scales with the length of its list, so the
    // speed is scaled by the same ratio.
    private void PlayOpeningReversed()
    {
        if (clip == null || clipFrames == null || clipFrames.Count == 0) return;

        int openingFrames = Mathf.Clamp(clip.phase2StartIndex, 0, clipFrames.Count);
        if (openingFrames == 0) return;

        List<Sprite> reversed = clipFrames.GetRange(0, openingFrames);
        reversed.Reverse();

        clip.StopAnimation();
        clip.textureArray = reversed;
        clip.animationMode = ImageAnimation.AnimationMode.SINGLE_PHASE;
        clip.doLoopAnimation = false;
        clip.AnimationSpeed = clipSpeed * openingFrames / clipFrames.Count;
        clip.StartAnimation();
    }

    // Only when the close swapped the frames — otherwise the clip is already as the scene has it.
    private void RestoreClip()
    {
        if (clip == null || clipFrames == null || clip.textureArray == clipFrames) return;

        clip.StopAnimation();
        clip.textureArray = clipFrames;
        clip.animationMode = clipMode;
        clip.doLoopAnimation = clipLoops;
        clip.AnimationSpeed = clipSpeed;
    }

    // Started explicitly rather than left to the component's StartOnEnable, so the sequence owns the
    // timing and a change to that checkbox cannot silently turn the animation off.
    private void StartClip()
    {
        if (clip != null) clip.StartAnimation();
    }

    private void KillScaleTween()
    {
        if (scaleTween != null) { scaleTween.Kill(); scaleTween = null; }
    }

    // How long the clip holds each frame: (1/24) × the WHOLE list's frame count ÷ AnimationSpeed, in
    // both phases (see ImageAnimation.CalculateFrameDelay).
    private float FrameTime()
    {
        if (clip == null || clip.textureArray == null || clip.textureArray.Count == 0) return 0f;

        return (1f / 24f) * clip.textureArray.Count / Mathf.Max(0.01f, clip.AnimationSpeed);
    }

    // The opening phase's length, worked out from the clip's own settings — used only for the cue's
    // timeout.
    private float OpeningDuration()
    {
        int openingFrames = Mathf.Clamp(clip.phase2StartIndex, 0, clip.textureArray.Count);
        int passes = clip.phase1LoopCount;

        return openingFrames * FrameTime() * passes + clip.delayBetweenLoop * Mathf.Max(0, passes - 1);
    }

    // A clip with an opening that ends. One set to loop forever (phase1LoopCount below 0) never
    // reaches the end of its opening, so it is treated like no clip at all.
    private bool HasOpeningPhase()
    {
        return clip != null
            && clip.animationMode == ImageAnimation.AnimationMode.TWO_PHASE
            && clip.textureArray != null && clip.textureArray.Count > 0
            && clip.phase1LoopCount >= 0;
    }
}
