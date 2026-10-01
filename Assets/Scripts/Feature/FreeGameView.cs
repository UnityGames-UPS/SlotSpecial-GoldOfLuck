using System;
using System.Collections;
using UnityEngine;

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
    [Tooltip("The panel above the slot for the whole round: a \"FREE GAME\" label plus the counter text. " +
             "The label is part of the panel — only the counter is written.")]
    [SerializeField] private GameObject counterPanel;

    [Tooltip("Written as \"remaining/total\" in a normal font, e.g. 10/10, 9/10 … 0/10.")]
    [SerializeField] private TMPro.TMP_Text counterText;

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
    [Tooltip("Seconds the congratulations panel stays up before closing by itself.")]
    [SerializeField] private float congratulationsHold = 2.5f;

    private Coroutine activeSequence;

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

        if (congratulationsHold > 0f) yield return new WaitForSeconds(congratulationsHold);

        // Snaps shut — a placeholder until the panel's own closing animation exists (ToDo.md).
        // Stopped explicitly: ImageAnimation drives itself with Invoke, so deactivating the object
        // is not a reliable way to end a looping clip.
        if (congratulationsPanelAnim != null) congratulationsPanelAnim.StopAnimation();
        congratulationsPanel.SetActive(false);

        activeSequence = null;
        onClosed?.Invoke();
    }

    #endregion

    #region Helpers

    private void WriteCounter(int remaining, int total)
    {
        if (counterText != null) counterText.text = remaining + "/" + total;
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
