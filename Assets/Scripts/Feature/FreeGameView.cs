using System;
using System.Collections;
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
/// stays active for the whole session, NOT to FreeGamesTexts or CongratulationsPanel themselves:
/// deactivating those would halt these coroutines mid-sequence.
/// </summary>
public class FreeGameView : MonoBehaviour
{
    [Header("Counter Panel")]
    [Tooltip("The FreeGamesTexts panel: the \"FREE GAME X/Y\" counter shown above the slot for the whole round.")]
    [SerializeField] private GameObject freeGamesTexts;
    [SerializeField] private CanvasGroup freeGamesTextsGroup;

    [Tooltip("Optional prompt pulsed beside the counter while the round waits for Start. Its own " +
             "CanvasGroup, because the pulse is on this alone rather than the whole panel.")]
    [SerializeField] private GameObject pressStartFeature;
    [SerializeField] private CanvasGroup pressStartFeatureGroup;

    [Tooltip("The FreeGamesRemaining parent. Its static \"FREE GAME ... / ...\" label needs no " +
             "reference — only the two numbers are written.")]
    [SerializeField] private GameObject freeGamesRemaining;
    [SerializeField] private TMPro.TMP_Text remainingFreeSpins;
    [SerializeField] private TMPro.TMP_Text totalFreeSpins;

    [Header("Congratulations Panel")]
    [Tooltip("\"CONGRATULATIONS / x / FREE GAME AWARDED!\". Opens, holds, and closes by itself.")]
    [SerializeField] private GameObject congratulationsPanel;
    [SerializeField] private CanvasGroup congratulationsPanelGroup;
    [Tooltip("The awarded spin count, the x in the panel. Written in sprite digits.")]
    [SerializeField] private TMPro.TMP_Text awardedSpinsText;
    [Tooltip("The panel's open-then-loop clip. Set it up as TWO_PHASE in the Inspector — the code " +
             "only starts and stops it.")]
    [SerializeField] private ImageAnimation congratulationsPanelAnim;

    // A documented exception to "tuning lives in code": judged by eye, so serialized, and the
    // scene's value is the one that runs.
    [Tooltip("Seconds the congratulations panel stays up before closing by itself.")]
    [SerializeField] private float congratulationsHold = 2.5f;

    // Deliberately NOT [SerializeField]. Serialized, the scene's saved values would silently
    // override any change made here. Code is the single source of truth.
    private const float promptPulseAlpha = 0.25f;      // alpha the prompt dips to
    private const float promptPulseDuration = 0.7f;
    private const float counterCountUpDuration = 1.0f;
    private const float panelFadeDuration = 0.3f;

    private Coroutine activeSequence;
    private Tween promptPulseTween;
    private Tween counterTween;
    private bool missingRefsLogged;

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

    /// <summary>
    /// The round is entered: the counter shows, with the Start prompt pulsing if one is wired. The
    /// Start button itself is UIManager's.
    /// </summary>
    internal void ShowCounter(int remaining, int total)
    {
        if (!HasRequiredRefs()) return;

        StopActiveSequence();
        if (counterTween != null) { counterTween.Kill(); counterTween = null; }

        SetGroupAlpha(freeGamesTextsGroup, 1f, true);
        freeGamesTexts.SetActive(true);
        ShowPanelState(prompt: true, remaining: true);
        WriteCounter(remaining, total);

        StartPromptPulse();
    }

    /// <summary>Start was pressed: the prompt goes, the counter stays.</summary>
    internal void OnFreeSpinsStarted()
    {
        StopPromptPulse();
        ShowPanelState(prompt: false, remaining: true);
    }

    // Both counters render in a sprite-digit font. ToSpriteDigits rather than ToSpriteMoney: these
    // are counts, and the money path would force MoneyFormat and show 6 spins as "6.00". Every write
    // goes through here — including each frame of the retrigger count-up — so no path can leave a
    // plain-text digit on a sprite font.
    private static string Digits(int count) => SpriteTextFormatter.ToSpriteDigits(count.ToString());

    /// <summary>Sets the counter with no animation. Called after every free spin.</summary>
    internal void UpdateCounter(int remaining, int total)
    {
        if (freeGamesTexts != null) freeGamesTexts.SetActive(true);
        ShowPanelState(prompt: false, remaining: true);
        WriteCounter(remaining, total);
    }

    /// <summary>
    /// Retrigger: animate the total up to its new value. The remaining count is already the
    /// post-retrigger figure and is shown at once.
    /// </summary>
    internal void AnimateTotalTo(int remaining, int fromTotal, int newTotal, Action onComplete)
    {
        if (!HasRequiredRefs() || totalFreeSpins == null)
        {
            onComplete?.Invoke();
            return;
        }

        StopActiveSequence();
        activeSequence = StartCoroutine(CountTotalRoutine(remaining, fromTotal, newTotal, onComplete));
    }

    /// <summary>The round is over — the counter goes. Called under the closing dim.</summary>
    internal void HideCounter()
    {
        StopActiveSequence();
        StopPromptPulse();
        if (counterTween != null) { counterTween.Kill(); counterTween = null; }

        ShowPanelState(prompt: false, remaining: false);
        if (freeGamesTexts != null) freeGamesTexts.SetActive(false);
        SetGroupAlpha(freeGamesTextsGroup, 0f, false);
    }

    /// <summary>Puts everything back the way the base game expects it. Safe to call at any point.</summary>
    internal void ResetToDefault()
    {
        HideCounter();

        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StopAnimation();
        if (congratulationsPanel != null) congratulationsPanel.SetActive(false);
        SetGroupAlpha(congratulationsPanelGroup, 0f, false);
    }

    #endregion

    #region Congratulations

    private IEnumerator CongratulationsRoutine(int spins, Action onClosed)
    {
        // Started explicitly rather than left to the component's StartOnEnable, so the sequence
        // owns the timing and a change to that checkbox cannot silently turn the animation off.
        AudioManager.Instance?.PlayCongratulations();
        congratulationsPanel.SetActive(true);
        SetGroupAlpha(congratulationsPanelGroup, 1f, true);
        if (awardedSpinsText != null) awardedSpinsText.text = Digits(spins);
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StartAnimation();

        if (congratulationsHold > 0f) yield return new WaitForSeconds(congratulationsHold);

        if (congratulationsPanelGroup != null)
        {
            yield return congratulationsPanelGroup.DOFade(0f, panelFadeDuration).WaitForCompletion();
        }

        // Stopped explicitly: ImageAnimation drives itself with Invoke, so deactivating the object
        // is not a reliable way to end a looping clip.
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StopAnimation();
        congratulationsPanel.SetActive(false);

        activeSequence = null;
        onClosed?.Invoke();
    }

    #endregion

    #region Counter

    private void WriteCounter(int remaining, int total)
    {
        if (remainingFreeSpins != null) remainingFreeSpins.text = Digits(remaining);
        if (totalFreeSpins != null) totalFreeSpins.text = Digits(total);
    }

    private IEnumerator CountTotalRoutine(int remaining, int fromTotal, int toTotal, Action onComplete)
    {
        if (freeGamesTexts != null) freeGamesTexts.SetActive(true);
        ShowPanelState(prompt: false, remaining: true);

        if (remainingFreeSpins != null) remainingFreeSpins.text = Digits(remaining);

        bool done = false;
        if (counterTween != null) counterTween.Kill();

        counterTween = DOVirtual.Int(fromTotal, toTotal, counterCountUpDuration, value =>
        {
            if (totalFreeSpins != null) totalFreeSpins.text = Digits(value);
        }).OnComplete(() =>
        {
            if (totalFreeSpins != null) totalFreeSpins.text = Digits(toTotal);
            counterTween = null;
            done = true;
        });

        yield return new WaitUntil(() => done);

        activeSequence = null;
        onComplete?.Invoke();
    }

    private void ShowPanelState(bool prompt, bool remaining)
    {
        if (pressStartFeature != null) pressStartFeature.SetActive(prompt);
        if (freeGamesRemaining != null) freeGamesRemaining.SetActive(remaining);
    }

    // Pulses the prompt alone, not the whole panel — the panel's own group is reserved for
    // showing and hiding the counter.
    private void StartPromptPulse()
    {
        StopPromptPulse();
        if (pressStartFeatureGroup == null) return;

        pressStartFeatureGroup.alpha = 1f;
        promptPulseTween = pressStartFeatureGroup
            .DOFade(promptPulseAlpha, promptPulseDuration)
            .SetEase(Ease.InOutSine)
            .SetLoops(-1, LoopType.Yoyo);
    }

    private void StopPromptPulse()
    {
        if (promptPulseTween != null)
        {
            promptPulseTween.Kill();
            promptPulseTween = null;
        }

        if (pressStartFeatureGroup != null) pressStartFeatureGroup.alpha = 1f;
    }

    #endregion

    #region Helpers

    private void SetGroupAlpha(CanvasGroup group, float alpha, bool active)
    {
        if (group == null) return;

        group.DOKill();
        group.alpha = alpha;
        group.gameObject.SetActive(active);
    }

    private void StopActiveSequence()
    {
        if (activeSequence != null)
        {
            StopCoroutine(activeSequence);
            activeSequence = null;
        }
    }

    // The scene UI is built after this script, so missing references are expected for a while.
    // Warn once, then let every sequence no-op straight to its callback so the round still runs.
    private bool HasRequiredRefs()
    {
        if (freeGamesTexts != null && freeGamesRemaining != null) return true;

        if (!missingRefsLogged)
        {
            missingRefsLogged = true;
            Debug.LogWarning("[FreeGameView] Counter references are not wired — free games will run without their counter.");
        }
        return false;
    }

    #endregion
}
