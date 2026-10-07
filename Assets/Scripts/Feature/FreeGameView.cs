using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

/// <summary>
/// Presentation for the Free Games round: the congratulations panel that announces the award, and
/// the "FREE GAME X/Y" counter above the slot. See Assets/Scripts/MD/genieWheel.md §3.7b–3.8.
///
/// Free Games is only ever entered from a Genie Wheel landing. The fades around the round and the
/// Winner panel at its end belong to GenieWheelView; this owns only the two panels above.
///
/// View layer only. GameManager drives this via the methods below and receives callbacks when a
/// sequence finishes — this script never calls back into the game loop. Attach to a GameObject that
/// stays active for the whole session, NOT to either panel: deactivating those would halt these
/// coroutines mid-sequence.
/// </summary>
public class FreeGameView : MonoBehaviour
{
    [Header("Counter")]
    [Tooltip("The panel above the slot for the whole round: a \"FREE GAME\" label, the two numbers, and a " +
             "static \"/\" between them. The label and the \"/\" are part of the panel — only the numbers are written.")]
    [SerializeField] private GameObject counterPanel;

    [Tooltip("Spins left, counting down: 10, 9 … 0. Sprite digits.")]
    [SerializeField] private TMPro.TMP_Text remainingSpinsText;

    [Tooltip("The round's total spins. Goes up on a retrigger. Sprite digits.")]
    [SerializeField] private TMPro.TMP_Text totalSpinsText;

    [Header("Congratulations Panel")]
    [Tooltip("\"CONGRATULATIONS / x / FREE GAMES AWARDED\". Scales up from nothing, plays its clip, shows its titles, " +
             "holds, and closes by itself.")]
    [SerializeField] private GameObject congratulationsPanel;

    [Tooltip("The awarded spin count — the x in the panel (FreeGameAmount). Visible from the start, scaling up with " +
             "the panel. Written in sprite digits through SpriteTextFormatter, so it needs the digit sprite asset.")]
    [SerializeField] private TMPro.TMP_Text awardedSpinsText;

    [Tooltip("The panel's open-then-loop clip. Set it up as TWO_PHASE in the Inspector — the code " +
             "only starts and stops it, and reads which frame it is showing to cue the titles.")]
    [SerializeField] private ImageAnimation congratulationsPanelAnim;

    // A documented exception to "tuning lives in code": judged by eye, so serialized, and the
    // scene's values are the ones that run.
    [Tooltip("Seconds the panel takes to scale up from 0 to its scene scale, with a slight overshoot. Its clip starts once it is full size.")]
    [SerializeField] private float panelScaleUpDuration = 0.35f;

    [Tooltip("How many clip frames before the scale-up finishes the panel's clip is started. ImageAnimation holds a " +
             "two-phase clip's first frame for about two frames before it moves, so starting it this early has frame 1 " +
             "land as the panel reaches full size. 0 = start it once the scale-up has finished.")]
    [SerializeField] private int clipEarlyStartFrames = 2;

    [Tooltip("Seconds the panel stays up, titles pulsing, once both titles have scaled up. Then it closes by itself.")]
    [SerializeField] private float titlesPulseHold = 2f;

    [Header("Congratulations Titles")]
    [Tooltip("The \"Congratulations\" text image. Scales up from 0 where it stands, Title Lead Frames before the " +
             "clip's opening phase ends, then pulses while the panel is up.")]
    [SerializeField] private RectTransform congratulationsTitle;

    [Tooltip("The \"Free Games Awarded\" text image. Scales up Title Stagger after Congratulations, then pulses.")]
    [SerializeField] private RectTransform freeGamesAwardedTitle;

    [Tooltip("How many clip frames before the end of the opening phase the titles start to scale up. Counted on the " +
             "frame actually on screen, so it holds whatever the clip's speed.")]
    [SerializeField] private int titleLeadFrames = 9;

    [Tooltip("Seconds Free Games Awarded starts after Congratulations.")]
    [SerializeField] private float titleStagger = 0.1f;

    [Tooltip("Seconds each title takes to scale up from 0 to full size, with a slight overshoot.")]
    [SerializeField] private float titlePopDuration = 0.35f;

    [Tooltip("The scale each title pulses up to after scaling up, and back down to 1, for as long as the panel is up.")]
    [SerializeField] private float titlePulseScale = 1.06f;

    [Tooltip("Seconds for one half of the pulse (1 up to Title Pulse Scale, or back down).")]
    [SerializeField] private float titlePulseDuration = 0.6f;

    [Header("Congratulations Closing")]
    [Tooltip("Seconds the panel takes to scale down to nothing, and the two titles to slide to the panel's middle " +
             "(y 0). All of it starts with the reverse of the clip's opening; the reverse holds its last frame until " +
             "the scale-down has finished, and then the panel goes.")]
    [SerializeField] private float panelScaleDownDuration = 0.8f;

    // Grace added to the clip's computed opening length before the title cue is given up on — the
    // clip's frames run a little long, and a clip that never plays must not hold the titles back.
    private const float TitleCueTimeoutMargin = 1f;

    private Coroutine activeSequence;
    private readonly List<Tween> titleTweens = new List<Tween>();
    private Tween panelScaleTween;

    // The panel's scene scale, read once at startup: the opening scales it from 0, so reading it on
    // each open could pick it up mid-scale.
    private Vector3 panelHomeScale = Vector3.one;

    // The titles' scene positions, read once at startup — the close slides them to y 0.
    private Vector2 congratulationsTitleHome;
    private Vector2 freeGamesAwardedTitleHome;

    // The clip's own setup, read at startup. The close borrows the component to play the opening
    // backwards, and these are put back afterwards so the next open plays normally.
    private List<Sprite> panelClipFrames;
    private ImageAnimation.AnimationMode panelClipMode;
    private float panelClipSpeed;
    private bool panelClipLoops;

    private void Awake()
    {
        if (congratulationsPanel != null) panelHomeScale = congratulationsPanel.transform.localScale;
        if (congratulationsTitle != null) congratulationsTitleHome = congratulationsTitle.anchoredPosition;
        if (freeGamesAwardedTitle != null) freeGamesAwardedTitleHome = freeGamesAwardedTitle.anchoredPosition;

        if (congratulationsPanelAnim != null)
        {
            panelClipFrames = congratulationsPanelAnim.textureArray;
            panelClipMode = congratulationsPanelAnim.animationMode;
            panelClipSpeed = congratulationsPanelAnim.AnimationSpeed;
            panelClipLoops = congratulationsPanelAnim.doLoopAnimation;
        }
    }

    #region Public API — called by GameManager

    /// <summary>
    /// The wheel landed on free games: the congratulations panel scales up with the awarded count,
    /// plays its opening, shows its titles, holds for titlesPulseHold, and closes by itself.
    /// onClosed fires once it has gone.
    /// </summary>
    internal void ShowCongratulations(int spins, Action onClosed)
    {
        StopActiveSequence();

        if (congratulationsPanel == null)
        {
            onClosed?.Invoke();
            return;
        }

        activeSequence = StartCoroutine(CongratulationsRoutine(spins, onClosed));
    }

    /// <summary>The round is entered: the counter panel shows.</summary>
    internal void ShowCounter(int remaining, int total)
    {
        if (counterPanel != null) counterPanel.SetActive(true);
        WriteCounter(remaining, total);
    }

    /// <summary>
    /// Sets the counter. Called as each free spin's result arrives, and after a retrigger — where
    /// the server has already added the extra spins, so the total simply goes up.
    /// </summary>
    internal void UpdateCounter(int remaining, int total)
    {
        WriteCounter(remaining, total);
    }

    /// <summary>The round is over — the counter goes. Called under the closing dim.</summary>
    internal void HideCounter()
    {
        if (counterPanel != null) counterPanel.SetActive(false);
    }

    #endregion

    #region Sequences

    // 1. The panel scales up from nothing with the spin count already on it; the titles wait at 0.
    // 2. The clip plays its opening, started clipEarlyStartFrames before the scale-up ends so its
    //    first movement lands as the panel reaches full size.
    // 3. titleLeadFrames before the opening ends, Congratulations scales up, Free Games Awarded a
    //    moment later; each pulses once it is full size.
    // 4. Once both are up, the panel holds for titlesPulseHold.
    // 5. The close: the clip's opening plays backwards while the panel scales down and the titles
    //    slide to y 0; once the scale-down has finished, the panel goes.
    private IEnumerator CongratulationsRoutine(int spins, Action onClosed)
    {
        StopCongratulationsAnimations();

        AudioManager.Instance?.PlayCongratulations();
        if (awardedSpinsText != null) awardedSpinsText.text = SpriteTextFormatter.ToSpriteDigits(spins.ToString());

        SetTitleScale(congratulationsTitle, 0f);
        SetTitleScale(freeGamesAwardedTitle, 0f);

        // Held on the clip's first frame while it scales, until the clip is started.
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.RevertToInitialState();

        Transform panel = congratulationsPanel.transform;
        panel.localScale = Vector3.zero;
        congratulationsPanel.SetActive(true);

        if (panelScaleUpDuration > 0f)
        {
            panelScaleTween = panel.DOScale(panelHomeScale, panelScaleUpDuration).SetEase(Ease.OutBack);

            // The clip starts partway through the scale-up: ImageAnimation draws a two-phase clip's
            // first frame twice before moving on, so started at full size it would sit still for
            // about two frames. Started this much early, frame 1 lands as the scale-up ends.
            float clipStartAt = Mathf.Max(0f, panelScaleUpDuration - ClipEarlyStartTime());
            if (clipStartAt > 0f) yield return new WaitForSeconds(clipStartAt);
            StartPanelClip();

            if (panelScaleTween.IsActive()) yield return panelScaleTween.WaitForCompletion();
            panelScaleTween = null;
        }
        else
        {
            StartPanelClip();
        }
        panel.localScale = panelHomeScale;

        yield return WaitForTitleCue();

        PopThenPulse(congratulationsTitle, 0f);
        PopThenPulse(freeGamesAwardedTitle, Mathf.Max(0f, titleStagger));

        float titlesUp = Mathf.Max(0f, titleStagger) + Mathf.Max(0.01f, titlePopDuration);
        yield return new WaitForSeconds(titlesUp + Mathf.Max(0f, titlesPulseHold));

        // The titles stop pulsing where they are; the scale-down carries them from there.
        AudioManager.Instance?.PlayCongratsClose();
        KillTitleTweens();
        PlayPanelClipReversed();

        if (panelScaleDownDuration > 0f)
        {
            // Linear, so it visibly shrinks from the first reversed frame. A wind-up ease (InBack) held
            // the panel at or above full size for the first ~60%, which read as starting after the reverse.
            panelScaleTween = panel.DOScale(0f, panelScaleDownDuration).SetEase(Ease.Linear);
            SlideTitleToMiddle(congratulationsTitle);
            SlideTitleToMiddle(freeGamesAwardedTitle);

            // The panel's three children shrink too, on top of the parent — so they go faster than
            // the panel and are gone a little ahead of it.
            ScaleDownWithPanel(congratulationsTitle);
            ScaleDownWithPanel(freeGamesAwardedTitle);
            ScaleDownWithPanel(awardedSpinsText != null ? awardedSpinsText.rectTransform : null);

            yield return panelScaleTween.WaitForCompletion();
            panelScaleTween = null;
        }

        // Stopped explicitly: ImageAnimation drives itself with Invoke, so deactivating the object
        // is not a reliable way to end a clip. StopCongratulationsAnimations puts the clip's own
        // frames back and every scale and position back to the scene's.
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StopAnimation();
        StopCongratulationsAnimations();
        congratulationsPanel.SetActive(false);

        activeSequence = null;
        onClosed?.Invoke();
    }

    // Plays the clip's opening backwards, once, on the same component: the opening frames reversed,
    // single-phase with looping off, so it stops and holds on the opening's first frame. Same
    // per-frame pace as the opening — a single-phase clip's frame time scales with the length of its
    // list, so the speed is scaled by the same ratio. RestorePanelClip puts the clip back.
    private void PlayPanelClipReversed()
    {
        ImageAnimation clip = congratulationsPanelAnim;
        if (clip == null || panelClipFrames == null || panelClipFrames.Count == 0) return;

        int openingFrames = Mathf.Clamp(clip.phase2StartIndex, 0, panelClipFrames.Count);
        if (openingFrames == 0) return;

        List<Sprite> reversed = panelClipFrames.GetRange(0, openingFrames);
        reversed.Reverse();

        clip.StopAnimation();
        clip.textureArray = reversed;
        clip.animationMode = ImageAnimation.AnimationMode.SINGLE_PHASE;
        clip.doLoopAnimation = false;
        clip.AnimationSpeed = panelClipSpeed * openingFrames / panelClipFrames.Count;
        clip.StartAnimation();
    }

    // Only when the close swapped the frames — otherwise the clip is already as the scene has it.
    private void RestorePanelClip()
    {
        ImageAnimation clip = congratulationsPanelAnim;
        if (clip == null || panelClipFrames == null || clip.textureArray == panelClipFrames) return;

        clip.StopAnimation();
        clip.textureArray = panelClipFrames;
        clip.animationMode = panelClipMode;
        clip.doLoopAnimation = panelClipLoops;
        clip.AnimationSpeed = panelClipSpeed;
    }

    private void SlideTitleToMiddle(RectTransform title)
    {
        if (title == null) return;

        titleTweens.Add(title.DOAnchorPosY(0f, panelScaleDownDuration).SetEase(Ease.InOutSine));
    }

    private void ScaleDownWithPanel(RectTransform child)
    {
        if (child == null) return;

        titleTweens.Add(child.DOScale(0f, panelScaleDownDuration).SetEase(Ease.Linear));
    }

    // Started explicitly rather than left to the component's StartOnEnable, so the sequence owns the
    // timing and a change to that checkbox cannot silently turn the animation off.
    private void StartPanelClip()
    {
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StartAnimation();
    }

    private float ClipEarlyStartTime()
    {
        ImageAnimation clip = congratulationsPanelAnim;
        if (clip == null || clip.textureArray == null || clip.textureArray.Count == 0) return 0f;

        return Mathf.Max(0, clipEarlyStartFrames) * FrameTime(clip);
    }

    // How long a clip holds each frame: (1/24) × the WHOLE list's frame count ÷ AnimationSpeed, in
    // both phases (see ImageAnimation.CalculateFrameDelay).
    private static float FrameTime(ImageAnimation clip)
    {
        return (1f / 24f) * clip.textureArray.Count / Mathf.Max(0.01f, clip.AnimationSpeed);
    }

    // Waits until the clip SHOWS the frame titleLeadFrames before its opening ends. Read off the
    // sprite on screen rather than timed: the clip's frames wait for rendered frames and run long,
    // which would throw a timed cue several frames out. Reached-or-passed, so a skipped frame can't
    // miss it. With no usable clip the titles go straight away, and a clip that never gets there is
    // given up on after its computed opening length plus a margin.
    private IEnumerator WaitForTitleCue()
    {
        ImageAnimation clip = congratulationsPanelAnim;
        if (!HasOpeningPhase(clip) || clip.rendererDelegate == null) yield break;

        int openingFrames = Mathf.Clamp(clip.phase2StartIndex, 0, clip.textureArray.Count);
        int cueFrame = Mathf.Max(0, openingFrames - Mathf.Max(0, titleLeadFrames));
        float timeout = OpeningDuration(clip) + TitleCueTimeoutMargin;

        for (float elapsed = 0f; elapsed < timeout; elapsed += Time.deltaTime)
        {
            if (clip.textureArray.IndexOf(clip.rendererDelegate.sprite) >= cueFrame) yield break;
            yield return null;
        }

        Debug.LogWarning("[FreeGameView] The congratulations clip never reached its title cue — the titles are shown anyway.");
    }

    // Scales up from 0 in place with a slight overshoot, then pulses. The pulse is a separate tween
    // started when the scale-up completes — an infinitely looping tween cannot sit inside a Sequence.
    private void PopThenPulse(RectTransform title, float delay)
    {
        if (title == null) return;

        title.localScale = Vector3.zero;

        Tween pop = title.DOScale(1f, Mathf.Max(0.01f, titlePopDuration))
            .SetEase(Ease.OutBack)
            .SetDelay(delay);
        pop.OnComplete(() =>
        {
            Tween pulse = title.DOScale(titlePulseScale, Mathf.Max(0.01f, titlePulseDuration))
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
            titleTweens.Add(pulse);
        });
        titleTweens.Add(pop);
    }

    // The opening phase's length, worked out from the clip's own settings — used only for the title
    // cue's timeout.
    private static float OpeningDuration(ImageAnimation clip)
    {
        int openingFrames = Mathf.Clamp(clip.phase2StartIndex, 0, clip.textureArray.Count);
        int passes = clip.phase1LoopCount;

        return openingFrames * FrameTime(clip) * passes + clip.delayBetweenLoop * Mathf.Max(0, passes - 1);
    }

    // A clip with an opening that ends. One set to loop forever (phase1LoopCount below 0) never
    // reaches the end of its opening, so it is treated like no clip at all.
    private static bool HasOpeningPhase(ImageAnimation clip)
    {
        return clip != null
            && clip.animationMode == ImageAnimation.AnimationMode.TWO_PHASE
            && clip.textureArray != null && clip.textureArray.Count > 0
            && clip.phase1LoopCount >= 0;
    }

    // Everything back as the scene has it — scales, the titles' positions, the clip's own frames — so
    // the panel never reopens mid-scale, mid-pulse or mid-close.
    private void StopCongratulationsAnimations()
    {
        if (panelScaleTween != null) { panelScaleTween.Kill(); panelScaleTween = null; }
        KillTitleTweens();
        RestorePanelClip();

        if (congratulationsPanel != null) congratulationsPanel.transform.localScale = panelHomeScale;
        SetTitleScale(congratulationsTitle, 1f);
        SetTitleScale(freeGamesAwardedTitle, 1f);
        if (awardedSpinsText != null) awardedSpinsText.rectTransform.localScale = Vector3.one;
        if (congratulationsTitle != null) congratulationsTitle.anchoredPosition = congratulationsTitleHome;
        if (freeGamesAwardedTitle != null) freeGamesAwardedTitle.anchoredPosition = freeGamesAwardedTitleHome;
    }

    // Killing a scale-up before it finishes also stops its pulse from ever starting, since a killed
    // tween never runs its OnComplete.
    private void KillTitleTweens()
    {
        foreach (var tween in titleTweens) tween?.Kill();
        titleTweens.Clear();
    }

    private static void SetTitleScale(RectTransform title, float scale)
    {
        if (title != null) title.localScale = Vector3.one * scale;
    }

    #endregion

    #region Helpers

    // Counts, not money — ToSpriteDigits rather than ToSpriteMoney, so 10 stays "10" and not "10.00".
    private void WriteCounter(int remaining, int total)
    {
        if (remainingSpinsText != null) remainingSpinsText.text = SpriteTextFormatter.ToSpriteDigits(remaining.ToString());
        if (totalSpinsText != null) totalSpinsText.text = SpriteTextFormatter.ToSpriteDigits(total.ToString());
    }

    private void StopActiveSequence()
    {
        if (activeSequence != null)
        {
            StopCoroutine(activeSequence);
            activeSequence = null;
        }
    }

    #endregion
}
