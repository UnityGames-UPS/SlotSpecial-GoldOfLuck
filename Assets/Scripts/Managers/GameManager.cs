using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GameManager : MonoBehaviour
{
    [Header("References")]
    [SerializeField] internal SocketIOManager socketManager;
    [SerializeField] internal UIManager uiManager;
    [SerializeField] private PopupManager popupManager;
    [SerializeField] private SlotView slotView;
    [SerializeField] private FreeGameView freeGameView;
    [SerializeField] private GenieWheelView genieWheelView;

    [Header("Spin Settings")]
    [SerializeField] private float normalSpinDuration = 3.5f;
    [SerializeField] private float turboSpinDuration = 2.0f;
    [SerializeField] private float quickSpinCycleDuration = 0.1f;

    [Header("Feature Timing")]
    [Tooltip("Genie Wheel trigger: how long after the Lamps start animating the full-screen Genie animation begins — the Lamps play their clip once and keep going underneath it. Retrigger: how long before the counter climbs.")]
    [SerializeField] private float scatterTriggerHold = 3.5f;
    [Tooltip("Retrigger only: how long the Lamps animate, in SlotView winSymbolLoopDuration units. The Genie Wheel trigger plays the Lamp clip once instead.")]
    [SerializeField] private int scatterTriggerLoops = 2;

    [Header("Win Settings")]
    // Win / total bet at or above which the big-win popup plays. Serialized, so the scene's value is
    // the one that runs — this default only matters for a fresh component. Kept equal to the scene.
    [SerializeField] private double bigWinMultiplierThreshold = 10.0;

    // Master switch for the Free Games round. The round is only ever entered from a Genie Wheel
    // free-games landing. Off, that landing is presented like a cash landing with nothing to pay, and
    // the server's free spins are then played as ordinary spins: the balance ends up right, but the
    // optimistic bet deduction in StartSpin is overwritten by the balance each response carries, so it
    // flickers. On is the only clean state against the live server.
    //
    // Un-serialized on purpose, like the other tuning constants: a serialized flag would be
    // overridden by whatever the scene saved. static readonly rather than const so the compiler does
    // not flag the code behind the switch as unreachable.
    internal static readonly bool FreeGamesEnabled = true;

    internal GameConfig gameConfig;
    internal PlayerData playerData;
    internal SpinResult lastResult;

    internal GameState currentState;
    internal SpinSpeed currentSpinSpeed;

    internal int currentBetIndex;
    internal double currentBetAmount;

    internal bool isAutoPlaying;
    internal int autoPlayTotalRounds;
    internal int autoPlayRemainingRounds;
    internal bool wasAutoPlayingBeforeFeature;
    internal int savedAutoPlayRemainingRounds;
    internal int savedAutoPlayTotalRounds;

    internal bool isInFreeSpins;
    internal int freeSpinsRemaining;      // server-authoritative, already decremented for this spin
    internal int freeSpinsUsed;           // counted here — one per free spin actually played
    internal double freeSpinsRoundWin;    // server-authoritative, from features.freeGame.totalRoundWin

    // Total spins the round has awarded, including every retrigger. Derived rather than tracked:
    // the server never sends an award size, but used + remaining is always the total, and it
    // self-corrects if a response is ever missed.
    internal int FreeSpinsTotalAwarded => freeSpinsUsed + freeSpinsRemaining;

    // A retrigger landed and its Lamps have not been shown yet — the counter waits for them.
    private bool retriggerPending;

    // The Genie Wheel owns the round from the trigger spin landing until its payout has been
    // presented (a cash landing) or the free-games round has been entered.
    internal bool isInGenieWheel;

    // The trigger spin is paid in two stages (genieWheel.md §3.10): at the trigger the balance and
    // win box show everything EXCEPT the wheel prize, which is added once the feature presents it.
    // Both stages are server figures — the interim balance is the server's balance minus the
    // server's prize. playerData itself always holds the true balance; only the display waits.
    internal bool withholdWheelPrize;
    private double heldWheelPrize;
    internal double DisplayBalance => withholdWheelPrize ? playerData.balance - heldWheelPrize : playerData.balance;

    private bool wheelStartPressed;
    private System.Action pendingWinnerTake;

    internal bool isInitialized;
    internal bool initializationFailed;

    private Coroutine spinCoroutine;
    private bool stopRequested;
    private bool waitingForSpecialWin;

    #region Initialization

    private void Start()
    {
        currentState = GameState.Initializing;
        currentSpinSpeed = SpinSpeed.Normal;
        isInitialized = false;
        initializationFailed = false;
    }

    internal void OnInitDataReceived(GameConfig config, PlayerData player, List<List<int>> initialMatrix)
    {
        gameConfig = config;
        playerData = player;
        currentBetIndex = playerData.currentBetIndex;
        UpdateBetAmount();

        if (initialMatrix != null && slotView != null)
        {
            slotView.SetInitialMatrix(initialMatrix);
        }

        if (genieWheelView != null) genieWheelView.SetSlices(config.wheelSlices, GetTotalPay());

        isInitialized = true;
        currentState = GameState.Idle;

        uiManager.OnGameInitialized();
    }

    #endregion

    #region Bet Management

    internal void IncreaseBet()
    {
        if (currentState != GameState.Idle || isAutoPlaying) return;
        if (gameConfig == null || gameConfig.availableBets == null || gameConfig.availableBets.Count == 0) return;

        int maxIndex = gameConfig.availableBets.Count - 1;
        int nextIndex = currentBetIndex + 1;
        if (nextIndex > maxIndex)
        {
            nextIndex = 0;
        }

        if (nextIndex == maxIndex)
        {
            AudioManager.Instance?.PlayMaxBetReached();
        }
        else
        {
            AudioManager.Instance?.PlayBetPlusMinus();
        }

        SetBetIndex(nextIndex);
    }

    internal void DecreaseBet()
    {
        if (currentState != GameState.Idle || isAutoPlaying) return;
        if (gameConfig == null || gameConfig.availableBets == null || gameConfig.availableBets.Count == 0) return;

        int maxIndex = gameConfig.availableBets.Count - 1;
        int nextIndex = currentBetIndex - 1;
        if (nextIndex < 0)
        {
            nextIndex = maxIndex;
        }

        if (nextIndex == maxIndex)
        {
            AudioManager.Instance?.PlayMaxBetReached();
        }
        else
        {
            AudioManager.Instance?.PlayBetPlusMinus();
        }

        SetBetIndex(nextIndex);
    }

    internal void SetBetIndex(int index)
    {
        currentBetIndex = index;
        UpdateBetAmount();
        uiManager.UpdateBetDisplay();
        if (slotView != null) slotView.OnBetChanged();
        if (genieWheelView != null) genieWheelView.OnBetChanged(GetTotalPay());
    }

    private void UpdateBetAmount()
    {
        currentBetAmount = gameConfig.availableBets[currentBetIndex];
    }

    #endregion

    #region Spin Control
    
    internal void RequestSpin()
    {
        if (currentState != GameState.Idle) return;
        if (!socketManager.isConnected) return;

        double totalPay = GetTotalPay();
        if (!isInFreeSpins && playerData.balance < totalPay)
        {
            if (popupManager != null)
            {
                popupManager.ShowInsufficientFundsError();
            }
            return;
        }

        StartSpin();
    }

    internal void RequestStop()
    {
        if (currentState == GameState.Spinning)
        {
            if (isAutoPlaying)
            {
                StopAutoPlay();
            }
            // A feature round cannot be stopped by hand — free games run themselves. A stop here
            // would also set stopRequested, which SlotView reads as a quick stop, and a round that
            // presents its own reels would be told to quick-stop reels that are not even spinning.
            else if (!isInFreeSpins)
            {
                stopRequested = true;
                uiManager.SetSpinStopButtonStates(isSpinningState: true, isInteractable: false);
            }
        }
    }

    private void StartSpin()
    {
        if (lastResult != null)
        {
            ProcessSpinResult();
        }

        lastResult = null;
        currentState = GameState.Spinning;
        stopRequested = false;

        // Deduct total pay from balance on spin start (except in free spins, which the server plays
        // for free — the triggering spin was the paid one).
        if (!isInFreeSpins)
        {
            playerData.balance -= GetTotalPay();
            if (playerData.balance < 0) playerData.balance = 0;
        }

        uiManager.OnSpinStarted();

        if (slotView != null)
        {
            slotView.StartSpin();
        }

        socketManager.SendSpinRequest(currentBetIndex);

        if (spinCoroutine != null)
            StopCoroutine(spinCoroutine);
        spinCoroutine = StartCoroutine(SpinRoutine());
    }

    private IEnumerator SpinRoutine()
    {
        float spinDuration = GetSpinDuration();
        float elapsed = 0f;

        while (elapsed < spinDuration && !stopRequested)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Player pressed Stop manually — hold for 0.5s so the reels keep
        // spinning briefly before snapping, giving clear visual feedback.
        if (stopRequested)
        {
            yield return new WaitForSeconds(0.5f);
        }

        while (lastResult == null)
        {
            yield return null;
        }

        currentState = GameState.Stopping;

        if (slotView != null && lastResult.resultMatrix != null)
        {
            if (currentSpinSpeed == SpinSpeed.QuickSpin || stopRequested)
            {
                // The view reports when the snap has settled, exactly as the normal stop does. This
                // used to wait a fixed 0.5s instead — a guess that only held because the reels
                // happen to land in 0.44s. Raise quickStopStagger or quickStopDuration in the
                // Inspector and the result was presented over a reel still landing, and the next
                // spin could start while SlotView still thought it was spinning, so its reels never
                // moved.
                slotView.QuickStop(lastResult.resultMatrix, OnReelsStoppedComplete);
            }
            else
            {
                slotView.StopSpin(lastResult.resultMatrix, OnReelsStoppedComplete);
            }
        }
        else
        {
            OnReelsStoppedComplete();
        }
    }

    private void OnReelsStoppedComplete()
    {
        // Safety net. StopSpinSequence already cuts the loop at the exact landing moment, but it is
        // bypassed entirely when SlotView has no reels or no result matrix to stop onto. Without this
        // the loop would run until the next spin restarted it. No-ops when already stopped.
        AudioManager.Instance?.StopSpinLoop();

        if (lastResult != null)
        {
            playerData = new PlayerData
            {
                balance = lastResult.playerData != null ? lastResult.playerData.balance : 0,
                currentBetIndex = lastResult.playerData != null ? lastResult.playerData.currentBetIndex : currentBetIndex
            };
        }

        // Decided once, here, before anything displays the balance: from this point the wheel's
        // prize is held back, and the wheel stops dead as the Lamps land.
        if (IsGenieWheelTrigger(lastResult))
        {
            isInGenieWheel = true;
            heldWheelPrize = lastResult.genieWheel.winAmount;
            withholdWheelPrize = true;
            if (genieWheelView != null) genieWheelView.Freeze();
        }

        // Kept as its own method, and reached by a plain call: anything that has to alter the board
        // between the reels landing and the win being presented belongs BEFORE this, running
        // PresentSpinOutcome from its own completion callback rather than alongside it. The board
        // has to be final before any win animation starts.
        PresentSpinOutcome();
    }

    // Everything that happens once the board is final.
    private void PresentSpinOutcome()
    {
        // A Genie Wheel trigger is not presented as a win at all — see PresentGenieWheelTrigger.
        if (isInGenieWheel)
        {
            PresentGenieWheelTrigger();
            return;
        }


        if (lastResult != null && lastResult.winAmount > 0 && lastResult.winLines != null && lastResult.winLines.Count > 0)
        {
            double totalPay = GetTotalPay();
            double multiplier = totalPay > 0 ? (lastResult.winAmount / totalPay) : 0;

            if (multiplier >= bigWinMultiplierThreshold)
            {
                uiManager.DisableControlsDuringWinAnimation();
                currentState = GameState.Idle;
                slotView.ShowWinLineAnimation(lastResult.winLines, OnWinAnimationComplete);
                StartCoroutine(TriggerWinPopupWithDelay(1.5f, lastResult));
            }
            else
            {
                // For normal wins, trigger UI update immediately and enable controls
                uiManager.OnSpinStopping(lastResult);
                uiManager.EnableControlsAfterWinAnimation();
                uiManager.OnSpinCompleted(lastResult);
                currentState = GameState.Idle;
                slotView.ShowWinLineAnimation(lastResult.winLines, OnWinAnimationComplete);
            }
        }
        else
        {
            uiManager.OnSpinStopping(lastResult);
            currentState = GameState.Idle;

            // Still handed to the view, just with nothing to present. A losing spin can arrive with
            // presentation state already raised — something earlier in the spin may have put the dim
            // up for a win that is now never coming — and clearing that is SlotView's call, not this
            // one's. Skipping the view here is what used to leave the board dimmed after a no-win.
            if (slotView != null)
            {
                slotView.ShowWinLineAnimation(null, OnWinAnimationComplete);
            }
            else
            {
                OnWinAnimationComplete();
            }
        }
    }

    private IEnumerator TriggerWinPopupWithDelay(float delay, SpinResult result)
    {
        double totalPay = GetTotalPay();
        double multiplier = totalPay > 0 ? (result.winAmount / totalPay) : 0;
        if (multiplier < bigWinMultiplierThreshold)
        {
            waitingForSpecialWin = false;
            yield break;
        }

        waitingForSpecialWin = true;

        yield return new WaitForSeconds(delay);

        if (lastResult == result && multiplier >= bigWinMultiplierThreshold)
        {
            uiManager.TriggerBigWinPopup(result, () =>
            {
                waitingForSpecialWin = false;
            });
        }
        else
        {
            waitingForSpecialWin = false;
        }
    }

    private void OnWinAnimationComplete()
    {
        if (lastResult != null)
        {
            double totalPay = GetTotalPay();
            double multiplier = totalPay > 0 ? (lastResult.winAmount / totalPay) : 0;

            // Only update UI here if it wasn't already updated in OnReelsStoppedComplete (multiplier < bigWinMultiplierThreshold)
            if (multiplier >= bigWinMultiplierThreshold)
            {
                uiManager.OnSpinStopping(lastResult);
            }
        }

        StartCoroutine(ProcessSpecialFeaturesAfterWin());
    }

    private IEnumerator ProcessSpecialFeaturesAfterWin()
    {
        // Wait for special win popup to finish before starting special features
        while (waitingForSpecialWin || uiManager.IsSpecialWinActive)
        {
            yield return null;
        }

        // The initial trigger never comes through here: a Genie Wheel trigger is taken over in
        // PresentSpinOutcome, and Free Games is only entered from the wheel.
        //
        // A retrigger — same scatter sequence, then the counter's total climbs to its new figure.
        // No prompt and no Start button; the round simply carries on.
        if (isInFreeSpins && retriggerPending)
        {
            yield return StartCoroutine(PlayRetriggerSequence());
        }

        ResumeAfterSpecialFeature();
    }

    private IEnumerator PlayRetriggerSequence()
    {
        retriggerPending = false;

        AudioManager.Instance?.PlayScatterTrigger();
        if (slotView != null) slotView.AnimateAllScatters(scatterTriggerLoops);

        yield return new WaitForSeconds(scatterTriggerHold);

        if (freeGameView != null) freeGameView.UpdateCounter(freeSpinsRemaining, FreeSpinsTotalAwarded);
    }

    private void ResumeAfterSpecialFeature()
    {
        if (isAutoPlaying || isInFreeSpins)
        {
            StartCoroutine(DelayBeforeNextRound());
        }
        else
        {
            ProcessSpinResult();
        }
    }

    private IEnumerator DelayBeforeNextRound()
    {
        float delayTime = currentSpinSpeed == SpinSpeed.QuickSpin ? 0.3f : 0.5f;
        yield return new WaitForSeconds(delayTime);

        // Wait for special win popup using the flag and active state
        while (waitingForSpecialWin || uiManager.IsSpecialWinActive)
        {
            yield return null;
        }

        ProcessSpinResult();
    }

    // If a feature round ever wants a spin duration of its own, shorten it by the same PROPORTION
    // turbo shortens a base spin rather than returning the base game's turbo duration directly —
    // that figure can easily be LONGER than the feature's own normal duration, which once made Turbo
    // and Quick Spin slower than Normal inside a round.
    private float GetSpinDuration()
    {
        return currentSpinSpeed switch
        {
            SpinSpeed.Normal => normalSpinDuration,
            SpinSpeed.Turbo => turboSpinDuration,
            SpinSpeed.QuickSpin => quickSpinCycleDuration,
            _ => normalSpinDuration
        };
    }

    internal void OnSpinResultReceived(SpinResult result)
    {
        lastResult = result;

        // The result is not handed to SlotView here. It writes the display-block sprites itself
        // when each reel lands, in StopSingleReel — in the same frame as the landing position
        // snap, so the swap is never on screen. An earlier "preload" wrote them mid-spin as well,
        // on a hand-tuned delay; it duplicated the landing write, was visible whenever the delay
        // missed its narrow window, and telegraphed the result each time the icons swept back
        // through the reel. Removed rather than retuned.

        // Update the round's numbers as soon as the response lands so the displays never lag the
        // reels. A retrigger needs no special handling: the server has already folded the extra
        // spins into spinsRemaining, so the count simply goes up instead of down.
        if (isInFreeSpins && result.freeGame != null)
        {
            freeSpinsUsed++;
            freeSpinsRemaining = result.freeGame.spinsRemaining;
            freeSpinsRoundWin = result.freeGame.roundWin;

            // An ordinary spin sets the counter now. A retrigger's new total waits until its Lamps
            // have animated — this only records that one is pending.
            if (result.freeGame.spinsAwarded)
            {
                retriggerPending = true;
            }
            else if (freeGameView != null)
            {
                freeGameView.UpdateCounter(freeSpinsRemaining, FreeSpinsTotalAwarded);
            }
        }
    }

    private void ProcessSpinResult()
    {
        playerData = lastResult.playerData;

        uiManager.OnSpinCompleted(lastResult);

        // This is where a round is advanced; the Genie Wheel is the one place a round is entered. A
        // feature added here should end its round on the server's own "still active" flag, never on
        // a spins-remaining counter reaching zero — a round can close on the very spin that reset its
        // counter, so the two do not agree.
        lastResult = null;

        if (isAutoPlaying && !isInFreeSpins)
        {
            if (autoPlayTotalRounds != -1)
            {
                autoPlayRemainingRounds--;
            }

            uiManager.UpdateAutoPlayCount();

            if (autoPlayTotalRounds != -1 && autoPlayRemainingRounds <= 0)
            {
                currentState = GameState.Idle;
                StopAutoPlay();
            }
            else
            {
                // Before requesting the next spin, verify the player can still afford it.
                // If not, stop autoplay (restores all UI) then show the popup.
                double totalPay = GetTotalPay();
                if (playerData.balance < totalPay)
                {
                    currentState = GameState.Idle;
                    StopAutoPlay();
                    if (popupManager != null) popupManager.ShowInsufficientFundsError();
                }
                else
                {
                    currentState = GameState.Idle;
                    RequestSpin();
                }
            }
        }
        else if (isInFreeSpins)
        {
            // Counters were updated in OnSpinResultReceived. spinsRemaining is the count *after*
            // this spin, so zero means the round is done — there is no separate round-over flag.
            if (freeSpinsRemaining <= 0)
            {
                EndFreeSpins();
            }
            else
            {
                currentState = GameState.Idle;
                StartCoroutine(DelayBeforeNextFreeSpin());
            }
        }
        else
        {
            currentState = GameState.Idle;
        }
    }

    #endregion

    #region Spin Speed Control

    internal void SetSpinSpeed(SpinSpeed speed)
    {
        currentSpinSpeed = speed;
    }

    #endregion



    #region Auto Play

    internal void StartAutoPlay(int rounds)
    {
        if (currentState != GameState.Idle) return;

        // Check balance BEFORE locking any UI — if insufficient, show popup and bail.
        double totalPay = GetTotalPay();
        if (playerData.balance < totalPay)
        {
            if (popupManager != null) popupManager.ShowInsufficientFundsError();
            return;
        }

        isAutoPlaying = true;
        autoPlayTotalRounds = rounds;
        autoPlayRemainingRounds = rounds;
        wasAutoPlayingBeforeFeature = false;

        uiManager.OnAutoPlayStarted();
        RequestSpin();
    }

    internal void StopAutoPlay()
    {
        isAutoPlaying = false;
        autoPlayRemainingRounds = 0;
        wasAutoPlayingBeforeFeature = false;

        uiManager.OnAutoPlayStopped();

        // Autoplay skips the per-line cycle while it runs, so the round it just finished is parked
        // after Phase 1. Now that no further spin is coming, present it the way a manual spin would.
        // Covers both endings: the last scheduled round, and the player stopping part-way.
        //
        // Feature rounds are excluded. A round that parks autoplay before it begins may well have
        // paid a line on its triggering spin, and cycling those lines here would run them underneath
        // the feature intro for its whole duration. The same goes for a Genie Wheel trigger, which
        // parks autoplay in ShowingWin: the cycle would tear the Lamp celebration down and animate a
        // ways win the trigger deliberately does not present.
        if (!isInFreeSpins && currentState != GameState.ShowingWin && slotView != null)
        {
            slotView.PlayWinLineCycle();
        }
    }

    internal bool ShouldResumeAutoPlay()
    {
        return wasAutoPlayingBeforeFeature && (savedAutoPlayTotalRounds == -1 || savedAutoPlayRemainingRounds > 0);
    }

    internal void ResumeAutoPlay()
    {
        if (!ShouldResumeAutoPlay()) return;

        int remaining = savedAutoPlayRemainingRounds;
        int total = savedAutoPlayTotalRounds;
        wasAutoPlayingBeforeFeature = false;

        if (currentState != GameState.Idle) return;

        double totalPay = GetTotalPay();
        if (playerData.balance < totalPay)
        {
            if (popupManager != null) popupManager.ShowInsufficientFundsError();
            return;
        }

        isAutoPlaying = true;
        autoPlayTotalRounds = total;
        autoPlayRemainingRounds = remaining;

        uiManager.OnAutoPlayStarted();
        RequestSpin();
    }

    #endregion

    #region Genie Wheel

    // Three Lamps on reels 3-5. The server has already resolved the wheel inside this same response,
    // so everything the feature presents is known now. A trigger during free spins is possible on the
    // server but not supported yet (ToDo.md): it is logged and presented as an ordinary free spin.
    private bool IsGenieWheelTrigger(SpinResult result)
    {
        if (result == null || result.genieWheel == null || !result.genieWheel.triggered) return false;

        if (isInFreeSpins)
        {
            Debug.LogWarning("[GameManager] Genie Wheel triggered during free spins — not supported yet, presented as an ordinary spin. See ToDo.md.");
            return false;
        }

        return true;
    }

    // The trigger spin owns the screen. Its ways win, if any, goes straight into the win box and the
    // balance with no animation and no big-win check — the wheel's prize is still held back. The
    // controller stays out of Idle for the whole feature, so every spin, bet and autoplay entry point
    // refuses input, and the buttons are locked to match.
    private void PresentGenieWheelTrigger()
    {
        // Before the suspend: StopAutoPlay replays the win-line cycle unless the state is ShowingWin.
        currentState = GameState.ShowingWin;
        SuspendAutoPlayForFeature();

        uiManager.OnSpinStopping(lastResult);
        uiManager.DisableControlsDuringWinAnimation();
        uiManager.SetFeatureButtonLock(true);

        StartCoroutine(GenieWheelRoutine(lastResult));
    }

    private IEnumerator GenieWheelRoutine(SpinResult result)
    {
        GenieWheelData wheel = result.genieWheel;

        // 1. The Lamps celebrate: their clip plays once, through to the end, and carries on under the
        //    Genie animation that starts after scatterTriggerHold — it is cleared beneath that
        //    animation's held frame, not here.
        AudioManager.Instance?.PlayScatterTrigger();
        if (slotView != null) slotView.PlayAllScattersOnce();
        yield return new WaitForSeconds(scatterTriggerHold);

        // 2. The full-screen animations. The board is cleared, and the stage swapped, beneath the
        //    first one's held frame. The wheel's music takes over as the first one starts, and plays
        //    until the board is reset to base — through the free spins too, if the wheel awards them.
        AudioManager.Instance?.PlayWheelBg();
        if (genieWheelView != null)
        {
            bool entered = false;
            genieWheelView.PlayEntryTransition(ClearTriggerBoard, () => entered = true);
            yield return new WaitUntil(() => entered);
            genieWheelView.ShowAwaitingStart();
        }
        else
        {
            ClearTriggerBoard();
        }

        // 3. Start. Autoplay is suspended, so only the player can press it.
        wheelStartPressed = false;
        uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.GenieWheelStart);
        yield return new WaitUntil(() => wheelStartPressed);
        uiManager.ReleaseSpinButton(interactable: false);

        // 4. The sweep, then the spin onto the slice the server already chose.
        if (genieWheelView != null)
        {
            bool swept = false;
            genieWheelView.PlayUndimSweep(() => swept = true);
            yield return new WaitUntil(() => swept);

            bool landed = false;
            genieWheelView.SpinToSlice(wheel.sliceIndex, () => landed = true);
            yield return new WaitUntil(() => landed);
        }

        if (wheel.type == WheelSliceType.FreeGames && FreeGamesEnabled)
        {
            yield return FreeGamesLandingRoutine(result);
        }
        else
        {
            yield return CashLandingRoutine(result);
        }
    }

    private void ClearTriggerBoard()
    {
        if (slotView != null) slotView.ClearTriggerAnimation();
    }

    // Coin or multiplier (genieWheel.md §3.7a): back to base under the dim, THEN the prize is paid —
    // balance, win box and Winner panel — and Take ends the feature.
    private IEnumerator CashLandingRoutine(SpinResult result)
    {
        GenieWheelData wheel = result.genieWheel;

        // Back to base at the dim's darkest point, the main music with it.
        System.Action resetToBase = () =>
        {
            if (genieWheelView != null) genieWheelView.ResetStageToBase(keepFeatureBackground: false);
            AudioManager.Instance?.PlayBgMusic();
        };

        if (genieWheelView != null)
        {
            bool reset = false;
            genieWheelView.PlayDimTransition(resetToBase, () => reset = true);
            yield return new WaitUntil(() => reset);
        }
        else
        {
            resetToBase();
        }

        // Second stage of the payout. The win box shows the wheel's prize alone — the server's
        // winInCash, never a figure read off the wheel.
        withholdWheelPrize = false;
        heldWheelPrize = 0;
        uiManager.UpdateBalanceDisplay();
        uiManager.ShowWinAmount(wheel.winAmount);

        if (wheel.winAmount > 0)
        {
            yield return WinnerPanelRoutine(wheel.winAmount);
        }

        FinishGenieWheelCash();
    }

    // Free games (genieWheel.md §3.7b): congratulations, then under the dim the wheel resets and the
    // slot returns while the special background stays, and the round waits for its own Start.
    private IEnumerator FreeGamesLandingRoutine(SpinResult result)
    {
        GenieWheelData wheel = result.genieWheel;

        // The counter runs on the server's figure; the congratulations panel shows the wheel's award.
        // They should always agree — said out loud if they ever don't.
        int spins = result.freeGame != null ? result.freeGame.spinsRemaining : wheel.freeGames;
        if (spins != wheel.freeGames)
        {
            Debug.LogWarning($"[GameManager] Wheel awarded {wheel.freeGames} free games but the round reports {spins} remaining. The counter follows the round.");
        }

        if (freeGameView != null)
        {
            bool closed = false;
            freeGameView.ShowCongratulations(wheel.freeGames, () => closed = true);
            yield return new WaitUntil(() => closed);
        }

        // A free-games landing pays nothing itself, so there is no second payout stage.
        withholdWheelPrize = false;
        heldWheelPrize = 0;

        System.Action underCover = () =>
        {
            if (genieWheelView != null) genieWheelView.ResetStageToBase(keepFeatureBackground: true);
            StartFreeSpins(spins);
        };

        if (genieWheelView != null)
        {
            bool entered = false;
            genieWheelView.PlayDimTransition(underCover, () => entered = true);
            yield return new WaitUntil(() => entered);
        }
        else
        {
            underCover();
        }

        // Free Games owns the round from here, and it starts itself — there is no Start button.
        // Cleared before the first spin, as ProcessSpinResult would: StartSpin would otherwise
        // process this result a second time. Idle because RequestSpin needs it; the locked buttons
        // and the greyed Spin button keep the player out.
        isInGenieWheel = false;
        lastResult = null;
        currentState = GameState.Idle;

        StartCoroutine(DelayBeforeFirstFreeSpin());
    }

    // Opens the Winner panel, makes Take pressable once the count-up finishes, and waits for it.
    // With no wheel view there is no panel to take, so nothing waits.
    private IEnumerator WinnerPanelRoutine(double amount)
    {
        if (genieWheelView == null) yield break;

        bool taken = false;
        pendingWinnerTake = () => taken = true;
        genieWheelView.ShowWinner(amount, () => uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.WinnerTake));
        yield return new WaitUntil(() => taken);

        bool closed = false;
        genieWheelView.CloseWinner(() => closed = true);
        yield return new WaitUntil(() => closed);
    }

    // Deliberately not ProcessSpinResult: its OnSpinCompleted would put the spin's grand total in the
    // win box, where the wheel's prize alone belongs.
    private void FinishGenieWheelCash()
    {
        isInGenieWheel = false;
        lastResult = null;

        uiManager.SetFeatureButtonLock(false);
        // Off the explicit Take mode without SetSpinButtonMode(Spin), which would clear the win box.
        uiManager.ReleaseSpinButton(interactable: true);
        uiManager.EnableControlsAfterWinAnimation();

        currentState = GameState.Idle;

        if (ShouldResumeAutoPlay())
        {
            ResumeAutoPlay();
        }
    }

    // Routed here by UIManager's GenieWheelStart mode.
    internal void OnGenieWheelStartPressed()
    {
        if (!isInGenieWheel) return;
        wheelStartPressed = true;
    }

    // Routed here by UIManager's WinnerTake mode — the Winner panel of either a cash landing or the
    // end of Free Games.
    internal void OnWinnerTakePressed()
    {
        var callback = pendingWinnerTake;
        pendingWinnerTake = null;
        callback?.Invoke();
    }

    // A feature parks autoplay for its whole length: Start and Take are the player's to press. The
    // trigger spin never reaches ProcessSpinResult's per-round decrement, so it is counted here.
    // StopAutoPlay clears the resume flag, so it is set after.
    private void SuspendAutoPlayForFeature()
    {
        if (!isAutoPlaying) return;

        int prevTotal = autoPlayTotalRounds;
        int prevRemaining = autoPlayRemainingRounds;

        StopAutoPlay();

        wasAutoPlayingBeforeFeature = true;
        savedAutoPlayTotalRounds = prevTotal;
        savedAutoPlayRemainingRounds = (prevTotal != -1) ? (prevRemaining - 1) : -1;
    }

    #endregion

    #region Free Spins

    // Entered only from a Genie Wheel free-games landing, beneath the full-screen dim. Autoplay was
    // already suspended at the wheel's trigger and resumes when this round ends. There is no Start
    // button: the first spin follows once the dim clears, and the rest play themselves.
    private void StartFreeSpins(int spins)
    {
        isInFreeSpins = true;
        freeSpinsRemaining = spins;
        freeSpinsUsed = 0;
        freeSpinsRoundWin = 0;
        retriggerPending = false;

        // No music change here: the wheel's music, started at the trigger, carries on through the round
        // and gives way to the main track under the end-of-round dim.

        if (freeGameView != null) freeGameView.ShowCounter(freeSpinsRemaining, FreeSpinsTotalAwarded);

        // The plain Spin button, greyed out for the round. Also zeroes the win box, which from here
        // shows the round's running total.
        uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.Spin, interactable: false);
    }

    private IEnumerator DelayBeforeFirstFreeSpin()
    {
        yield return new WaitForSeconds(0.5f);
        RequestSpin();
    }

    private IEnumerator DelayBeforeNextFreeSpin()
    {
        yield return new WaitForSeconds(0.3f);

        // Wait for special win popup if it's still active or pending
        while (waitingForSpecialWin || uiManager.IsSpecialWinActive)
        {
            yield return null;
        }

        RequestSpin();
    }

    // The round is over. The Winner panel counts the round's total up BEFORE the fade back to base —
    // the reverse of a cash landing — and Take both closes it and starts the fade.
    //
    // The last spin's lines are not cycled here, unlike Golden Dynasty: free spins show no line walk
    // (genieWheel.md §3.7b), and the cycle would carry on over the base game after the fade.
    private void EndFreeSpins()
    {
        double roundWin = freeSpinsRoundWin;

        isInFreeSpins = false;
        freeSpinsRemaining = 0;

        // Out of Idle until the round has been taken and faded away.
        currentState = GameState.ShowingWin;

        StartCoroutine(FreeGamesEndRoutine(roundWin));
    }

    private IEnumerator FreeGamesEndRoutine(double roundWin)
    {
        yield return WinnerPanelRoutine(roundWin);

        System.Action underCover = () =>
        {
            if (genieWheelView != null) genieWheelView.SetFeatureBackground(false);
            if (freeGameView != null) freeGameView.HideCounter();
            AudioManager.Instance?.PlayBgMusic();
        };

        if (genieWheelView != null)
        {
            bool faded = false;
            genieWheelView.PlayDimTransition(underCover, () => faded = true);
            yield return new WaitUntil(() => faded);
        }
        else
        {
            underCover();
        }

        OnFreeGamesOutroComplete();
    }

    // Player took the win and the closing fade finished — restore the base game.
    private void OnFreeGamesOutroComplete()
    {
        freeSpinsUsed = 0;
        freeSpinsRoundWin = 0;
        retriggerPending = false;

        uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.Spin);
        uiManager.SetFeatureButtonLock(false);

        currentState = GameState.Idle;

        if (ShouldResumeAutoPlay())
        {
            ResumeAutoPlay();
        }
    }

    #endregion

    #region Connection Events

    internal void OnDisconnected()
    {
        if (spinCoroutine != null)
        {
            StopCoroutine(spinCoroutine);
            spinCoroutine = null;
        }

        wasAutoPlayingBeforeFeature = false;
        if (isAutoPlaying)
        {
            StopAutoPlay();
        }

        currentState = GameState.Idle;
        // Note: The disconnection popup is shown by SocketIOManager.OnSocketDisconnected()
        // to avoid duplicates. GameManager only cleans up state here.
    }

    internal void ExitGame()
    {
        socketManager.CloseSocket();

    }

    #endregion

    #region Helper Methods

    internal double GetTotalPay()
    {
        double activeLine = (gameConfig != null && gameConfig.activeLine > 0) ? gameConfig.activeLine : 50;
        return currentBetAmount * activeLine;
    }

    internal bool IsSpinning()
    {
        return currentState == GameState.Spinning || currentState == GameState.Stopping;
    }

    #endregion
}