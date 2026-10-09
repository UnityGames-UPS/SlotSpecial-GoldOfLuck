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
    [Tooltip("Seconds the panel takes to scale up from 0 to its scene scale, with a slight overshoot. Its clip starts Clip Early Start Frames before the scale-up ends.")]
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

    // The panel's scale-up / reverse-and-scale-down choreography, shared with the Winner panel. Made
    // in Awake, which is when it reads the panel's scene scale and the clip's own setup.
    private PanelClipPlayback congratulationsClip;

    // The titles' scene positions, read once at startup — the close slides them to y 0.
    private Vector2 congratulationsTitleHome;
    private Vector2 freeGamesAwardedTitleHome;

    private void Awake()
    {
        congratulationsClip = new PanelClipPlayback(
            congratulationsPanel != null ? congratulationsPanel.transform : null, congratulationsPanelAnim);

        if (congratulationsTitle != null) congratulationsTitleHome = congratulationsTitle.anchoredPosition;
        if (freeGamesAwardedTitle != null) freeGamesAwardedTitleHome = freeGamesAwardedTitle.anchoredPosition;
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

        // Activated first, in the same frame the scale-up sets it to 0, so it never shows at full size.
        congratulationsPanel.SetActive(true);
        yield return congratulationsClip.ScaleUpAndPlay(panelScaleUpDuration, clipEarlyStartFrames);

        yield return congratulationsClip.WaitForOpeningCue(titleLeadFrames, TitleCueTimeoutMargin, nameof(FreeGameView));

        PopThenPulse(congratulationsTitle, 0f);
        PopThenPulse(freeGamesAwardedTitle, Mathf.Max(0f, titleStagger));

        float titlesUp = Mathf.Max(0f, titleStagger) + Mathf.Max(0.01f, titlePopDuration);
        yield return new WaitForSeconds(titlesUp + Mathf.Max(0f, titlesPulseHold));

        // The titles stop pulsing where they are; the scale-down carries them from there.
        AudioManager.Instance?.PlayCongratsClose();
        KillTitleTweens();

        // The reverse and the panel's scale-down start together (see PanelClipPlayback).
        Tween scaleDown = congratulationsClip.ReverseAndScaleDown(panelScaleDownDuration);
        if (scaleDown != null)
        {
            SlideTitleToMiddle(congratulationsTitle);
            SlideTitleToMiddle(freeGamesAwardedTitle);

            // The panel's three children shrink too, on top of the parent — so they go faster than
            // the panel and are gone a little ahead of it.
            ScaleDownWithPanel(congratulationsTitle);
            ScaleDownWithPanel(freeGamesAwardedTitle);
            ScaleDownWithPanel(awardedSpinsText != null ? awardedSpinsText.rectTransform : null);

            yield return scaleDown.WaitForCompletion();
        }

        // StopCongratulationsAnimations puts the clip's own frames back and every scale and position
        // back to the scene's.
        congratulationsClip.StopClip();
        StopCongratulationsAnimations();
        congratulationsPanel.SetActive(false);

        activeSequence = null;
        onClosed?.Invoke();
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

    // Everything back as the scene has it — scales, the titles' positions, the clip's own frames — so
    // the panel never reopens mid-scale, mid-pulse or mid-close.
    private void StopCongratulationsAnimations()
    {
        congratulationsClip.Reset();
        KillTitleTweens();

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
