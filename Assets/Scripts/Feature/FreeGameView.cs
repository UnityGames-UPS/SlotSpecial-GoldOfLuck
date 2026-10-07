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
    [Tooltip("\"CONGRATULATIONS / x / FREE GAMES AWARDED\". Opens, holds, and closes by itself.")]
    [SerializeField] private GameObject congratulationsPanel;

    [Tooltip("The awarded spin count — the x in the panel. Normal font.")]
    [SerializeField] private TMPro.TMP_Text awardedSpinsText;

    [Tooltip("The panel's open-then-loop clip. Set it up as TWO_PHASE in the Inspector — the code " +
             "only starts and stops it.")]
    [SerializeField] private ImageAnimation congratulationsPanelAnim;

    // A documented exception to "tuning lives in code": judged by eye, so serialized, and the
    // scene's value is the one that runs.
    [Tooltip("Seconds the congratulations panel stays up before closing by itself. The titles' pop-in counts within it.")]
    [SerializeField] private float congratulationsHold = 2.5f;

    [Header("Congratulations Titles")]
    [Tooltip("The \"Congratulations\" text image. Pops in from nothing, then pulses while the panel is up.")]
    [SerializeField] private RectTransform congratulationsTitle;

    [Tooltip("The \"Free Games Awarded\" text image. Pops in with the other title, at the same moment.")]
    [SerializeField] private RectTransform freeGamesAwardedTitle;

    // Feel, tuned by eye — serialized like congratulationsHold, so the scene's values win.
    [Tooltip("Seconds for each title to pop in from scale 0 to full size (with a slight overshoot).")]
    [SerializeField] private float titlePopDuration = 0.35f;

    [Tooltip("The scale each title pulses up to after popping in, and back down to 1, for as long as the panel is up.")]
    [SerializeField] private float titlePulseScale = 1.06f;

    [Tooltip("Seconds for one half of the pulse (1 up to Title Pulse Scale, or back down).")]
    [SerializeField] private float titlePulseDuration = 0.6f;

    private Coroutine activeSequence;
    private readonly List<Tween> titleTweens = new List<Tween>();

    #region Public API — called by GameManager

    /// <summary>
    /// The wheel landed on free games: the congratulations panel opens with the awarded count,
    /// holds for congratulationsHold, and closes by itself. onClosed fires once it has gone.
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

    /// <summary>Puts everything back the way the base game expects it. Safe to call at any point.</summary>
    internal void ResetToDefault()
    {
        StopActiveSequence();
        HideCounter();

        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StopAnimation();
        StopTitleAnimations();
        if (congratulationsPanel != null) congratulationsPanel.SetActive(false);
    }

    #endregion

    #region Sequences

    private IEnumerator CongratulationsRoutine(int spins, Action onClosed)
    {
        // Started explicitly rather than left to the component's StartOnEnable, so the sequence
        // owns the timing and a change to that checkbox cannot silently turn the animation off.
        AudioManager.Instance?.PlayCongratulations();
        congratulationsPanel.SetActive(true);
        if (awardedSpinsText != null) awardedSpinsText.text = spins.ToString();
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StartAnimation();
        StartTitleAnimations();

        if (congratulationsHold > 0f) yield return new WaitForSeconds(congratulationsHold);

        // Snaps shut — a placeholder until the panel's own closing animation exists (ToDo.md).
        // Stopped explicitly: ImageAnimation drives itself with Invoke, so deactivating the object
        // is not a reliable way to end a looping clip.
        AudioManager.Instance?.PlayCongratsClose();
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StopAnimation();
        StopTitleAnimations();
        congratulationsPanel.SetActive(false);

        activeSequence = null;
        onClosed?.Invoke();
    }

    // Both titles pop in together from scale 0, then pulse. The pulse is a separate tween started when
    // the pop completes — an infinitely looping tween cannot sit inside a DOTween Sequence. The awarded
    // spin count between them is deliberately left still (owner).
    private void StartTitleAnimations()
    {
        StopTitleAnimations();
        PopThenPulse(congratulationsTitle);
        PopThenPulse(freeGamesAwardedTitle);
    }

    private void PopThenPulse(RectTransform title)
    {
        if (title == null) return;

        title.localScale = Vector3.zero;

        Tween pop = title.DOScale(1f, Mathf.Max(0.01f, titlePopDuration)).SetEase(Ease.OutBack);
        pop.OnComplete(() =>
        {
            Tween pulse = title.DOScale(titlePulseScale, Mathf.Max(0.01f, titlePulseDuration))
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
            titleTweens.Add(pulse);
        });
        titleTweens.Add(pop);
    }

    // Killing the pop before it finishes also stops its pulse from ever starting, since a killed
    // tween never runs its OnComplete. Scales go back to 1 so the panel never reopens mid-pulse.
    private void StopTitleAnimations()
    {
        foreach (var tween in titleTweens) tween?.Kill();
        titleTweens.Clear();

        if (congratulationsTitle != null) congratulationsTitle.localScale = Vector3.one;
        if (freeGamesAwardedTitle != null) freeGamesAwardedTitle.localScale = Vector3.one;
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
