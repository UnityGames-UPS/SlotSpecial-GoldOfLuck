using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using TMPro;

/// <summary>
/// Presentation for the Genie Wheel feature: the wheel itself (wedge labels, the dim mask and its
/// undim sweep, result spin, landing highlight) and the stage around it (the two full-screen animations, the
/// background swap, switching SlotObject off and on, the full-screen dim, and the Winner panel).
/// See Assets/Scripts/MD/genieWheel.md for the sequence this implements.
///
/// View layer only. GameManager decides when each step runs and what the wheel lands on; this
/// never reads the controller — total bet is passed in — and every sequence ends in the callback
/// it was given. DEGRADATION IS A RULE: an unwired reference or a clip with no frames falls
/// straight through to its callback, so a missing asset can never stall the round.
///
/// Lives on its own always-active object (GenieWheelManager), never on Wheel, WheelBackground or
/// SlotObject: switching this object off would halt its coroutines mid-sequence.
///
/// ONE WRITER AT A TIME. The background drift (ContinuousRotator) and the result spin write the
/// same localRotation, so every path that moves the wheel stops the drift first, and only
/// ResumeIdle hands it back.
/// </summary>
public class GenieWheelView : MonoBehaviour
{
    // Child names looked up under each Slice_ (n). They match the scene as built; resolving them
    // by name saves wiring two references on each of eighteen slices.
    private const string AmountTextName = "AmountText";
    private const string FreeSpinAmountTextName = "FreeSpinAmountText";   // the free-games wedges' count
    private const string MultiplierTextName = "MultiplierText";

    // The "X" on a multiplier badge. WheelBrownNumbers (the badges' sprite asset) holds it at index 11,
    // after the digits 0-9 and the decimal point at 10. Only that asset has it.
    private const string MultiplierSign = "<sprite=11>";

    // Must match ImageAnimation's own idealFrameRate. A clip of n frames at speed s lasts
    // n² × this ÷ s — quadratic, see GameExplanation.md §10.
    private const float ClipFrameTime = 0.0416666679f;

    // Grace added to a one-shot clip's computed length before it is given up on. Covers a clip
    // that never starts (no renderer, inactive parent) so the round carries on regardless.
    private const float OneShotTimeoutMargin = 1f;

    private const float WinnerFadeDuration = 0.3f;

    [Header("Wheel")]
    [Tooltip("The ContinuousRotator that drifts the wheel all session. Stopped for the feature and restarted by ResumeIdle.")]
    [SerializeField] private ContinuousRotator rotator;

    [Tooltip("The rotating wheel (the object the ContinuousRotator turns). Its pivot must be (0.5, 0.5).")]
    [SerializeField] private RectTransform wheel;

    [Tooltip("The Slices container. Its children are read in sibling order as slices 0..17, and each child's " +
             "\"AmountText\" (or \"FreeSpinAmountText\" on a free-games wedge) and \"MultiplierText\" are found " +
             "by name. Any may be missing.")]
    [SerializeField] private Transform slicesRoot;

    [Tooltip("WheelMask: the dark overlay over the wheel, inside Wheel after Slices so it turns with it. " +
             "Image Type Filled, Radial 360, origin Top, Fill Clockwise OFF — lowering the fill then lights the " +
             "wedges clockwise. Fill 1 = every wedge dim, 0 = none. The code switches it on under Genie " +
             "animation 1's held frame and off when the sweep finishes — save it off in the scene.")]
    [SerializeField] private Image wheelMask;

    [Header("Wheel Animations")]
    [Tooltip("Loop around the green centre circle while the feature waits for Start. Must be its own object — it is hidden whenever it isn't playing.")]
    [SerializeField] private ImageAnimation centreLoop;

    [Tooltip("Loop on the outside of the wheel. Starts with the undim sweep (Start pressed) and runs until the background returns to base — after a cash landing, or at the end of the free-games round. Its own object, hidden when not playing.")]
    [SerializeField] private ImageAnimation outerLoop;

    [Tooltip("Smoke travelling clockwise over the wheel during the undim sweep. Played once; its speed is SET BY CODE so it lasts exactly Undim Sweep Duration.")]
    [SerializeField] private ImageAnimation smokeSweep;

    [Tooltip("Loop on the wheel while it spins. Its own object over the wheel — never the wheel's own Image, which would be hidden with it.")]
    [SerializeField] private ImageAnimation spinLoop;

    [Tooltip("Loop on the winning wedge. Sits still over the pointer, OUTSIDE Wheel: the winning wedge always ends under the pointer.")]
    [SerializeField] private ImageAnimation winHighlight;

    [Header("Stage")]
    [Tooltip("The main slot and its furniture. Switched off for the wheel and back on after it.")]
    [SerializeField] private GameObject slotObject;

    [Tooltip("The background Image. Its sprite is swapped to Feature Background for the feature and restored to whatever it started with.")]
    [SerializeField] private Image backgroundImage;
    [SerializeField] private Sprite featureBackground;

    [Tooltip("Full-screen animation 1. Played once and held on its last frame while the stage changes underneath. Forced to single-phase, no loop.")]
    [SerializeField] private ImageAnimation fullScreenAnim1;

    [Tooltip("Full-screen animation 2. Played once as animation 1 is hidden, then hidden itself. Forced to single-phase, no loop.")]
    [SerializeField] private ImageAnimation fullScreenAnim2;

    [Tooltip("The full-screen dim. Faded up, the stage changes under it, then faded away.")]
    [SerializeField] private CanvasGroup fullScreenDim;

    [Header("Winner Panel")]
    [SerializeField] private GameObject winnerPanel;
    [SerializeField] private CanvasGroup winnerPanelGroup;

    [Tooltip("The amount that counts up. Written in sprite digits, like the big-win popup.")]
    [SerializeField] private TMP_Text winnerAmountText;

    [Tooltip("The panel's open-then-loop clip. Set it up as TWO_PHASE in the Inspector — the code only starts and stops it.")]
    [SerializeField] private ImageAnimation winnerPanelAnim;

    // ── Feel ────────────────────────────────────────────────────────────────────────────────────
    // A documented exception to "tuning lives in code": these can only be judged by eye, so they
    // are serialized and the SCENE's values are the ones that run.
    [Header("Tuning")]
    [Tooltip("Length of the result spin in seconds. Fixed whatever the outcome, so the duration never hints at the result.")]
    [SerializeField] private float spinDuration = 6f;

    [Tooltip("Shape of the result spin: progress (0..1) over time (0..1). Must start at 0 and end at 1. " +
             "Flat at both ends = starts from rest and settles; the straight middle is the full-speed hold.")]
    [SerializeField] private AnimationCurve spinCurve = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 0f),
        new Keyframe(0.2f, 0.1333f, 1.3333f, 1.3333f),
        new Keyframe(0.7f, 0.8f, 1.3333f, 1.3333f),
        new Keyframe(1f, 1f, 0f, 0f));

    [Tooltip("Whole turns added to every spin. Enough of them keeps the apparent speed steady whatever the target.")]
    [SerializeField] private int fullTurns = 5;

    [Tooltip("The wheel rotation (Z) that puts slice 0 under the pointer. The scene's slices start at -10°, so 10.")]
    [SerializeField] private float sliceZeroAngle = 10f;

    [Tooltip("Slices numbered clockwise around the rim.")]
    [SerializeField] private bool slicesClockwise = true;

    [Tooltip("The result spin turns clockwise.")]
    [SerializeField] private bool spinClockwise = true;

    [Tooltip("Length of the undim sweep. THE single pace-setter: the smoke clip's speed is computed from it.")]
    [SerializeField] private float undimSweepDuration = 2.5f;

    [Tooltip("How long the winning-wedge highlight plays before the feature moves on.")]
    [SerializeField] private float winHighlightHold = 2f;

    [Tooltip("Full-screen dim fade, each way.")]
    [SerializeField] private float dimFadeDuration = 0.4f;

    [Tooltip("How long the full-screen dim holds, fully up, while the stage changes beneath it.")]
    [SerializeField] private float dimHoldDuration = 0.3f;

    [Tooltip("Winner panel count-up length.")]
    [SerializeField] private float winnerCountUpDuration = 2f;

    [Header("Testing")]
    [Tooltip("Slice for the right-click \"Test Spin\" menu. Play mode only; needs no backend.")]
    [SerializeField] private int testSliceIndex;

    private class SliceRefs
    {
        public Transform root;
        public TMP_Text amountText;
        public TMP_Text multiplierText;
    }

    private readonly List<SliceRefs> sliceRefs = new List<SliceRefs>();
    private List<WheelSlice> slices;
    private Sprite baseBackground;

    private Coroutine stageSequence;
    private Coroutine wheelSequence;
    private Coroutine winnerSequence;
    private Tween spinTween;
    private Tween sweepTween;
    private Tween winnerCountTween;
    private bool isSpinning;
    private bool curveWarningLogged;

    // The backend's slice list when the init has arrived, the scene's slices before that — so
    // "Test Spin" works with no backend at all.
    private int SliceCount => slices != null && slices.Count > 0 ? slices.Count : sliceRefs.Count;
    private float SliceStep => 360f / Mathf.Max(1, SliceCount);

    #region Unity lifecycle

    private void Awake()
    {
        ResolveSlices();

        if (backgroundImage != null) baseBackground = backgroundImage.sprite;

        if (wheel != null && (wheel.pivot - new Vector2(0.5f, 0.5f)).sqrMagnitude > 0.0001f)
        {
            Debug.LogWarning($"[GenieWheelView] The wheel's pivot is {wheel.pivot}, not (0.5, 0.5) — it will spin off-centre.");
        }

        // Feature-only elements start hidden, whatever the scene was saved with.
        StopLoop(centreLoop);
        StopLoop(outerLoop);
        StopLoop(smokeSweep);
        StopLoop(spinLoop);
        StopLoop(winHighlight);
        StopLoop(fullScreenAnim1);
        StopLoop(fullScreenAnim2);
        SetGroup(fullScreenDim, 0f, false);
        if (winnerPanel != null) winnerPanel.SetActive(false);

        // Off and undimmed whatever the scene was saved with — the base game shows a lit wheel.
        if (wheelMask != null && wheelMask.type != Image.Type.Filled)
        {
            Debug.LogWarning("[GenieWheelView] Wheel Mask is not a Filled image — the undim sweep can't step it. Set Image Type to Filled, Radial 360.");
        }
        SetMaskFill(0f);
        SetMaskVisible(false);
    }

    private void OnDestroy()
    {
        spinTween?.Kill();
        sweepTween?.Kill();
        winnerCountTween?.Kill();
        if (fullScreenDim != null) fullScreenDim.DOKill();
        if (winnerPanelGroup != null) winnerPanelGroup.DOKill();
    }

    #endregion

    #region Labels — called by GameManager

    /// <summary>
    /// At init: stores the backend's slices and writes every wedge label at the current total bet.
    /// The labels are a projection only — the prize shown after a landing is always the server's.
    /// </summary>
    internal void SetSlices(List<WheelSlice> wheelSlices, double totalBet)
    {
        slices = wheelSlices;

        if (slices == null || slices.Count == 0)
        {
            Debug.LogError("[GenieWheelView] The init carried no wheel slices — the wedges keep their scene text.");
            return;
        }

        if (slices.Count != sliceRefs.Count)
        {
            Debug.LogError($"[GenieWheelView] The backend sends {slices.Count} wheel slices but the scene has {sliceRefs.Count}. " +
                           "The wheel will land on the wrong wedges until these match.");
        }

        for (int i = 0; i < slices.Count; i++)
        {
            if (slices[i].sliceIndex != i)
            {
                Debug.LogError($"[GenieWheelView] Slice at position {i} has sliceIndex {slices[i].sliceIndex}. The wheel assumes rim order = sliceIndex.");
            }

            if (slices[i].type == WheelSliceType.Multiplier && (i >= sliceRefs.Count || sliceRefs[i].multiplierText == null))
            {
                Debug.LogError($"[GenieWheelView] Slice {i} is a MULTIPLIER slice but has no \"{MultiplierTextName}\" child.");
            }

            // A MULTIPLIER slice pays coin × multiplier × total bet, so it needs its coin. The backend
            // once sent coin 0 on all six; a regression would otherwise just show 0.00 on the wedge.
            if (slices[i].type == WheelSliceType.Multiplier && slices[i].coin <= 0)
            {
                Debug.LogError($"[GenieWheelView] Slice {i} is a MULTIPLIER slice with no coin value — its wedge cannot show what it pays. Backend data?");
            }

            // Every wedge writes its value into an amount text, found by name. A renamed child would
            // otherwise leave the scene's placeholder showing with no sign anything was wrong.
            if (i < sliceRefs.Count && sliceRefs[i].amountText == null)
            {
                Debug.LogError($"[GenieWheelView] Slice {i} has no \"{AmountTextName}\" or \"{FreeSpinAmountTextName}\" child — its value is not written.");
            }
        }

        WriteLabels(totalBet);
    }

    /// <summary>The bet changed — every cash label is re-projected at the new total bet.</summary>
    internal void OnBetChanged(double totalBet)
    {
        WriteLabels(totalBet);
    }

    private void WriteLabels(double totalBet)
    {
        if (slices == null) return;

        int count = Mathf.Min(slices.Count, sliceRefs.Count);
        for (int i = 0; i < count; i++)
        {
            WheelSlice slice = slices[i];
            SliceRefs refs = sliceRefs[i];

            // Every label is drawn in one of the wheel's sprite-digit fonts — WheelWhiteNumbers for
            // cash, WheelYellowNumbers for the free-games count, WheelBrownNumbers for the multiplier
            // badge — so each goes through ToSpriteDigits, which passes the stacking line breaks
            // through untouched.
            switch (slice.type)
            {
                case WheelSliceType.Coin:
                    SetText(refs.amountText, SpriteTextFormatter.ToSpriteDigits(
                        Stacked((slice.coin * totalBet).ToString(SpriteTextFormatter.MoneyFormat))));
                    break;

                case WheelSliceType.Multiplier:
                    SetText(refs.amountText, SpriteTextFormatter.ToSpriteDigits(
                        Stacked((slice.multiplier * totalBet).ToString(SpriteTextFormatter.MoneyFormat))));
                    SetText(refs.multiplierText, MultiplierSign + SpriteTextFormatter.ToSpriteDigits(slice.multiplier.ToString()));
                    break;

                case WheelSliceType.FreeGames:
                    // A count, not money — ToSpriteDigits rather than ToSpriteMoney, so 8 stays "8"
                    // and not "8.00". The scene's "FREE" label beside it stays as built.
                    SetText(refs.amountText, SpriteTextFormatter.ToSpriteDigits(slice.freeGames.ToString()));
                    break;
            }
        }
    }

    // Cash amounts read down the wedge, one character per line: "1.05" becomes "1\n.\n0\n5". Only
    // the cash — the multiplier badge and the free-games count stay on one line. The label grows
    // with the bet (4 lines at the lowest, 7 for 3000.00 at the highest), so the AmountText boxes
    // need room for 7 lines or TMP Auto Size.
    private static string Stacked(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length * 2);
        for (int i = 0; i < text.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    #endregion

    #region Feature sequence — called by GameManager, in this order

    /// <summary>The Lamps have landed: the drift stops dead, with no wind-down.</summary>
    internal void Freeze()
    {
        if (rotator != null) rotator.StopRotating();
    }

    /// <summary>
    /// Full-screen animation 1 plays and holds its last frame. Under that frame, underCover runs
    /// first (the controller clears the board), then the background swaps, SlotObject goes off and
    /// the wheel is snapped to a known angle with every wedge dimmed. Animation 2 then plays out.
    /// </summary>
    internal void PlayEntryTransition(Action underCover, Action onComplete)
    {
        Freeze();
        StopSequence(ref stageSequence);
        stageSequence = StartCoroutine(EntryRoutine(underCover, onComplete));
    }

    /// <summary>The wheel waits for the player: the centre circle's loop starts.</summary>
    internal void ShowAwaitingStart()
    {
        StartLoop(centreLoop);
    }

    /// <summary>
    /// After Start: the outer loop starts and the smoke travels clockwise while the mask's fill drops
    /// one wedge (1/18) at a time, lighting the wedges in turn from slice 0.
    /// </summary>
    internal void PlayUndimSweep(Action onComplete)
    {
        StopSequence(ref wheelSequence);
        wheelSequence = StartCoroutine(SweepRoutine(onComplete));
    }

    /// <summary>
    /// The result spin. Lands exactly on sliceIndex, starts the winning-wedge highlight, holds it
    /// for winHighlightHold, then calls back. Refuses — logs and calls back at once — when there
    /// is no wheel, the slice is out of range, or a spin is already running.
    /// </summary>
    internal void SpinToSlice(int sliceIndex, Action onPresented)
    {
        if (wheel == null || isSpinning || sliceIndex < 0 || sliceIndex >= SliceCount)
        {
            Debug.LogWarning($"[GenieWheelView] Spin to slice {sliceIndex} refused " +
                             (wheel == null ? "(no wheel wired)." : isSpinning ? "(already spinning)." : $"(out of range, {SliceCount} slices)."));
            onPresented?.Invoke();
            return;
        }

        StopSequence(ref wheelSequence);
        wheelSequence = StartCoroutine(SpinRoutine(sliceIndex, onPresented));
    }

    /// <summary>
    /// The full-screen dim fades up, underCover runs while it covers the screen, and it fades away.
    /// With no dim wired, underCover and onComplete simply run in turn.
    /// </summary>
    internal void PlayDimTransition(Action underCover, Action onComplete)
    {
        StopSequence(ref stageSequence);

        if (fullScreenDim == null)
        {
            underCover?.Invoke();
            onComplete?.Invoke();
            return;
        }

        stageSequence = StartCoroutine(DimRoutine(underCover, onComplete));
    }

    /// <summary>
    /// Back to the base game's stage: SlotObject on, the wheel idle and drifting, and the base
    /// background — unless keepFeatureBackground, which the free-games round keeps.
    /// </summary>
    internal void ResetStageToBase(bool keepFeatureBackground)
    {
        SetSlotVisible(true);
        if (!keepFeatureBackground) SetFeatureBackground(false);
        ResumeIdle();
    }

    /// <summary>
    /// Swaps the background sprite. The free-games round restores it at its own end. The outer loop
    /// belongs to the feature background, not the wheel, so it goes when the background returns to
    /// base — which for a free-games landing is the end of the round, not the wheel's reset.
    /// </summary>
    internal void SetFeatureBackground(bool on)
    {
        if (!on) StopLoop(outerLoop);

        if (backgroundImage == null) return;

        if (on && featureBackground != null) backgroundImage.sprite = featureBackground;
        else if (!on && baseBackground != null) backgroundImage.sprite = baseBackground;
    }

    /// <summary>
    /// Hands the wheel back to the drift: every wheel animation stopped and hidden, the mask cleared
    /// so every wedge is lit, and the rotator restarted from wherever the wheel now stands. The outer
    /// loop is left alone — it stops with the background (SetFeatureBackground).
    /// </summary>
    internal void ResumeIdle()
    {
        bool wasSpinning = isSpinning;

        StopSequence(ref wheelSequence);
        if (spinTween != null) { spinTween.Kill(); spinTween = null; }
        if (sweepTween != null) { sweepTween.Kill(); sweepTween = null; }
        isSpinning = false;

        StopLoop(centreLoop);
        StopLoop(smokeSweep);
        StopLoop(spinLoop);
        StopLoop(winHighlight);
        SetMaskFill(0f);
        SetMaskVisible(false);

        // A spin cut short here must not leave its sound running. Only when a spin WAS running: the
        // sound shares the reel spin's source, and an unconditional stop could cut the reels' sound.
        if (wasSpinning) AudioManager.Instance?.StopWheelSpin();

        if (rotator != null) rotator.StartRotating();
    }

    #endregion

    #region Winner panel — called by GameManager

    /// <summary>
    /// Opens the Winner panel and counts the amount up from 0. onCountUpComplete is the
    /// controller's cue to put Take on the Spin button; the panel's clip keeps looping until
    /// CloseWinner.
    /// </summary>
    internal void ShowWinner(double amount, Action onCountUpComplete)
    {
        StopSequence(ref winnerSequence);
        if (winnerCountTween != null) { winnerCountTween.Kill(); winnerCountTween = null; }

        if (winnerPanel == null)
        {
            onCountUpComplete?.Invoke();
            return;
        }

        winnerPanel.SetActive(true);
        SetGroup(winnerPanelGroup, 1f, true);
        StartLoop(winnerPanelAnim);
        AudioManager.Instance?.PlayWinner();

        if (winnerAmountText == null)
        {
            onCountUpComplete?.Invoke();
            return;
        }

        winnerAmountText.text = SpriteTextFormatter.ToSpriteMoney(0);

        winnerCountTween = DOVirtual.Float(0f, (float)amount, Mathf.Max(0.01f, winnerCountUpDuration), value =>
        {
            if (winnerAmountText != null) winnerAmountText.text = SpriteTextFormatter.ToSpriteMoney(value);
        }).OnComplete(() =>
        {
            // Snapped to the exact server figure — the float tween only approximates it.
            if (winnerAmountText != null) winnerAmountText.text = SpriteTextFormatter.ToSpriteMoney(amount);
            winnerCountTween = null;
            onCountUpComplete?.Invoke();
        });
    }

    /// <summary>Take was pressed: the Winner panel fades out and closes.</summary>
    internal void CloseWinner(Action onClosed)
    {
        StopSequence(ref winnerSequence);
        if (winnerCountTween != null) { winnerCountTween.Kill(); winnerCountTween = null; }

        if (winnerPanel == null || !winnerPanel.activeSelf)
        {
            StopLoop(winnerPanelAnim);
            onClosed?.Invoke();
            return;
        }

        winnerSequence = StartCoroutine(CloseWinnerRoutine(onClosed));
    }

    #endregion

    #region Routines

    private IEnumerator EntryRoutine(Action underCover, Action onComplete)
    {
        yield return PlayOnce(fullScreenAnim1, null);

        underCover?.Invoke();
        SetFeatureBackground(true);
        SetSlotVisible(false);
        PrepareWheelForFeature();

        // Animation 1's held frame gives way the moment animation 2 begins.
        yield return PlayOnce(fullScreenAnim2, () => StopLoop(fullScreenAnim1));

        StopLoop(fullScreenAnim1);
        StopLoop(fullScreenAnim2);

        stageSequence = null;
        onComplete?.Invoke();
    }

    private IEnumerator SweepRoutine(Action onComplete)
    {
        StopLoop(centreLoop);
        StartLoop(outerLoop);
        AudioManager.Instance?.PlayWheelSweep();

        float duration = Mathf.Max(0.01f, undimSweepDuration);

        // The code's duration is the pace-setter: the smoke's speed is derived from it so the clip
        // lasts exactly as long, rather than the two being tuned by eye and drifting apart.
        if (HasFrames(smokeSweep))
        {
            int frames = smokeSweep.textureArray.Count;
            smokeSweep.gameObject.SetActive(true);
            smokeSweep.animationMode = ImageAnimation.AnimationMode.SINGLE_PHASE;
            smokeSweep.doLoopAnimation = false;
            smokeSweep.onLoopComplete = null;
            smokeSweep.AnimationSpeed = ClipFrameTime * frames * frames / duration;
            smokeSweep.StartAnimation();
        }

        // One whole wedge at a time: the fill only ever drops in exact 1/steps steps, and each wedge
        // lights once the smoke has crossed it. The mask's own origin and direction (see its tooltip)
        // decide where the sweep starts and which way it runs — the smoke clip must match them.
        int steps = Mathf.Max(1, SliceCount);

        sweepTween = DOVirtual.Float(0f, 1f, duration, progress =>
        {
            int lit = Mathf.Min(steps, Mathf.FloorToInt(progress * steps));
            SetMaskFill(1f - (float)lit / steps);
        }).SetEase(Ease.Linear);

        yield return sweepTween.WaitForCompletion();
        sweepTween = null;

        // Whatever the tween's last step didn't reach. Every wedge is lit, so the mask goes — the wheel
        // spins with nothing over it.
        SetMaskFill(0f);
        SetMaskVisible(false);

        StopLoop(smokeSweep);

        wheelSequence = null;
        onComplete?.Invoke();
    }

    private IEnumerator SpinRoutine(int sliceIndex, Action onPresented)
    {
        isSpinning = true;
        Freeze();
        StopLoop(winHighlight);
        StartLoop(spinLoop);
        AudioManager.Instance?.PlayWheelSpin();

        float start = wheel.localEulerAngles.z;
        float target = AngleForSlice(sliceIndex);

        // The landing angle is computed once, here, and eased to exactly. The travel to it varies
        // by less than one turn, so with fullTurns on top the apparent speed barely changes.
        float end;
        if (spinClockwise)
        {
            float travel = Mathf.Repeat(start - target, 360f);
            end = start - travel - 360f * Mathf.Max(0, fullTurns);
        }
        else
        {
            float travel = Mathf.Repeat(target - start, 360f);
            end = start + travel + 360f * Mathf.Max(0, fullTurns);
        }

        spinTween = DOVirtual.Float(0f, 1f, Mathf.Max(0.01f, spinDuration),
                t => SetWheelAngle(Mathf.LerpUnclamped(start, end, EvaluateSpinCurve(t))))
            .SetEase(Ease.Linear);

        yield return spinTween.WaitForCompletion();
        spinTween = null;

        SetWheelAngle(end);
        StopLoop(spinLoop);
        AudioManager.Instance?.StopWheelSpin();
        isSpinning = false;

        StartLoop(winHighlight);
        AudioManager.Instance?.PlayWheelWin();
        if (winHighlightHold > 0f) yield return new WaitForSeconds(winHighlightHold);

        wheelSequence = null;
        onPresented?.Invoke();
    }

    private IEnumerator DimRoutine(Action underCover, Action onComplete)
    {
        fullScreenDim.DOKill();
        fullScreenDim.gameObject.SetActive(true);
        fullScreenDim.alpha = 0f;

        yield return fullScreenDim.DOFade(1f, dimFadeDuration).WaitForCompletion();

        underCover?.Invoke();
        if (dimHoldDuration > 0f) yield return new WaitForSeconds(dimHoldDuration);

        yield return fullScreenDim.DOFade(0f, dimFadeDuration).WaitForCompletion();
        fullScreenDim.gameObject.SetActive(false);

        stageSequence = null;
        onComplete?.Invoke();
    }

    private IEnumerator CloseWinnerRoutine(Action onClosed)
    {
        if (winnerPanelGroup != null)
        {
            winnerPanelGroup.DOKill();
            yield return winnerPanelGroup.DOFade(0f, WinnerFadeDuration).WaitForCompletion();
        }

        StopLoop(winnerPanelAnim);
        if (winnerPanel != null) winnerPanel.SetActive(false);

        winnerSequence = null;
        onClosed?.Invoke();
    }

    // Plays a clip once and waits for its last frame, which it then holds. onStarted runs the
    // moment it begins — or at once, if there is nothing to play. A clip that never finishes is
    // given up on after its computed length plus a margin, so it cannot stall the feature.
    private IEnumerator PlayOnce(ImageAnimation anim, Action onStarted)
    {
        if (!HasFrames(anim))
        {
            onStarted?.Invoke();
            yield break;
        }

        bool done = false;
        anim.gameObject.SetActive(true);
        anim.animationMode = ImageAnimation.AnimationMode.SINGLE_PHASE;
        anim.doLoopAnimation = false;
        anim.onLoopComplete = _ => done = true;
        anim.StartAnimation();
        onStarted?.Invoke();

        int frames = anim.textureArray.Count;
        float timeout = ClipFrameTime * frames * frames / Mathf.Max(0.01f, anim.AnimationSpeed) + OneShotTimeoutMargin;
        float elapsed = 0f;
        while (!done && elapsed < timeout)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        anim.onLoopComplete = null;
        if (!done) Debug.LogWarning($"[GenieWheelView] '{anim.name}' never finished — carrying on without it.");
    }

    #endregion

    #region Wheel helpers

    // Behind animation 1's held frame — the one moment the wheel is fully covered — so the snap
    // is never seen. A known start angle means the sweep always runs over the same wedges.
    private void PrepareWheelForFeature()
    {
        Freeze();
        if (spinTween != null) { spinTween.Kill(); spinTween = null; }
        isSpinning = false;

        SetWheelAngle(sliceZeroAngle);
        StopLoop(winHighlight);

        // The dim goes up here, with the background swap — every wedge dark while the wheel waits
        // for Start. The fill only starts dropping once Start begins the sweep.
        SetMaskFill(1f);
        SetMaskVisible(true);
    }

    private float AngleForSlice(int sliceIndex)
    {
        return sliceZeroAngle + (slicesClockwise ? 1f : -1f) * SliceStep * sliceIndex;
    }

    private float EvaluateSpinCurve(float t)
    {
        if (spinCurve == null || spinCurve.length < 2)
        {
            return Mathf.SmoothStep(0f, 1f, t);
        }

        if (!curveWarningLogged && (Mathf.Abs(spinCurve.Evaluate(0f)) > 0.001f || Mathf.Abs(spinCurve.Evaluate(1f) - 1f) > 0.001f))
        {
            curveWarningLogged = true;
            Debug.LogWarning("[GenieWheelView] Spin Curve should run from 0 to 1 — the wheel will jump at the start or end of the spin.");
        }

        return spinCurve.Evaluate(t);
    }

    private void SetWheelAngle(float z)
    {
        if (wheel != null) wheel.localRotation = Quaternion.Euler(0f, 0f, z);
    }

    private void SetMaskFill(float fill)
    {
        if (wheelMask != null) wheelMask.fillAmount = Mathf.Clamp01(fill);
    }

    // The mask is switched on and off explicitly rather than left active with a fill of 0: only the
    // feature should ever draw it, whatever state the scene was saved in.
    private void SetMaskVisible(bool visible)
    {
        if (wheelMask != null) wheelMask.gameObject.SetActive(visible);
    }

    private void ResolveSlices()
    {
        sliceRefs.Clear();

        if (slicesRoot == null)
        {
            Debug.LogWarning("[GenieWheelView] Slices root is not wired — the wedges keep their scene labels.");
            return;
        }

        for (int i = 0; i < slicesRoot.childCount; i++)
        {
            Transform child = slicesRoot.GetChild(i);
            // The free-games wedges name their count differently; either name fills the same slot.
            Transform amount = child.Find(AmountTextName);
            if (amount == null) amount = child.Find(FreeSpinAmountTextName);
            Transform multiplier = child.Find(MultiplierTextName);

            sliceRefs.Add(new SliceRefs
            {
                root = child,
                amountText = amount != null ? amount.GetComponent<TMP_Text>() : null,
                multiplierText = multiplier != null ? multiplier.GetComponent<TMP_Text>() : null
            });
        }
    }

    #endregion

    #region Stage helpers

    private void SetSlotVisible(bool visible)
    {
        if (slotObject != null) slotObject.SetActive(visible);
    }

    private static bool HasFrames(ImageAnimation anim)
    {
        return anim != null && anim.textureArray != null && anim.textureArray.Count > 0;
    }

    // Loops keep their own Inspector setup (mode, speed, loop flag) — the code only starts, stops
    // and shows them.
    private static void StartLoop(ImageAnimation anim)
    {
        if (!HasFrames(anim)) return;

        anim.gameObject.SetActive(true);
        anim.StartAnimation();
    }

    private static void StopLoop(ImageAnimation anim)
    {
        if (anim == null) return;

        anim.StopAnimation();
        anim.gameObject.SetActive(false);
    }

    private static void SetGroup(CanvasGroup group, float alpha, bool active)
    {
        if (group == null) return;

        group.DOKill();
        group.alpha = alpha;
        group.gameObject.SetActive(active);
    }

    private static void SetText(TMP_Text text, string value)
    {
        if (text != null) text.text = value;
    }

    private void StopSequence(ref Coroutine sequence)
    {
        if (sequence == null) return;

        StopCoroutine(sequence);
        sequence = null;
    }

    #endregion

    #region Testing

    [ContextMenu("Test Spin")]
    private void TestSpin()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[GenieWheelView] Test Spin runs in Play mode only.");
            return;
        }

        SpinToSlice(testSliceIndex, () => Debug.Log($"[GenieWheelView] Test Spin landed on slice {testSliceIndex}."));
    }

    [ContextMenu("Test Resume Idle")]
    private void TestResumeIdle()
    {
        if (Application.isPlaying) ResumeIdle();
    }

    #endregion
}
