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

    [Header("Spin Settings")]
    [SerializeField] private float normalSpinDuration = 3.5f;
    [SerializeField] private float turboSpinDuration = 2.0f;
    [SerializeField] private float quickSpinCycleDuration = 0.1f;

    [Header("Free Games Timing")]
    [Tooltip("How long the scatters animate before the award prompt or, on a retrigger, before the counter climbs.")]
    [SerializeField] private float scatterTriggerHold = 3.5f;
    [Tooltip("Scatter animation loops on a retrigger. The initial trigger uses 0 (runs until the first free spin starts) because the player controls when that ends.")]
    [SerializeField] private int scatterTriggerLoops = 2;

    [Header("Win Settings")]
    [SerializeField] private double bigWinMultiplierThreshold = 500.0;

    // Master switch for the Free Games round, OFF while the backend binding is brought up. Off means
    // the round is never entered: the trigger spin is presented as an ordinary spin and
    // ProcessSpinResult carries on. Nothing is removed — the view, the round state and the lifecycle
    // below are all intact, so flipping this to true restores the feature as it was.
    //
    // Off is not a clean state against the live server. A wheel landing on a free-games slice still
    // puts the SERVER into a round, which this client then plays as normal spins: the server treats
    // them as free, so the balance ends up right, but the optimistic bet deduction in StartSpin is
    // overwritten by the balance the response carries, so it flickers.
    //
    // Un-serialized on purpose, like the other tuning constants: a serialized flag would be
    // overridden by whatever the scene saved. static readonly rather than const so the compiler does
    // not flag the code behind the switch as unreachable.
    internal static readonly bool FreeGamesEnabled = false;

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
    internal bool wasAutoPlayingBeforeFreeSpins;
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

    // The total the counter was showing before a retrigger landed, so its count-up has somewhere to
    // start from. -1 when no retrigger is pending presentation.
    private int retriggerTotalBefore = -1;

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

        // Kept as its own method, and reached by a plain call: anything that has to alter the board
        // between the reels landing and the win being presented belongs BEFORE this, running
        // PresentSpinOutcome from its own completion callback rather than alongside it. The board
        // has to be final before any win animation starts.
        PresentSpinOutcome();
    }

    // Everything that happens once the board is final.
    private void PresentSpinOutcome()
    {
        // A spin that awards Free Games is not over once its outcome is presented: the Scatters
        // still celebrate for scatterTriggerHold, and only then does ProcessSpinResult enter the
        // round. Returning to Idle here left that whole hold open to a Spin press, which entered
        // the round early and started a free spin with no prompt — and when the hold ended,
        // ProcessSpinResult consumed THAT spin's result, leaving its reels spinning forever.
        //
        // So the controller stays out of Idle until the round is entered. Every spin, bet and
        // autoplay entry point already refuses anything but Idle; whatever enters the round —
        // StartFreeSpins today — is what puts it back.
        GameState settledState = IsFreeGamesTrigger(lastResult) ? GameState.ShowingWin : GameState.Idle;

        if (lastResult != null && lastResult.winAmount > 0 && lastResult.winLines != null && lastResult.winLines.Count > 0)
        {
            double totalPay = GetTotalPay();
            double multiplier = totalPay > 0 ? (lastResult.winAmount / totalPay) : 0;

            if (multiplier >= bigWinMultiplierThreshold)
            {
                uiManager.DisableControlsDuringWinAnimation();
                currentState = settledState;
                slotView.ShowWinLineAnimation(lastResult.winLines, OnWinAnimationComplete);
                StartCoroutine(TriggerWinPopupWithDelay(1.5f, lastResult));
            }
            else
            {
                // For normal wins, trigger UI update immediately and enable controls
                uiManager.OnSpinStopping(lastResult);
                uiManager.EnableControlsAfterWinAnimation();
                uiManager.OnSpinCompleted(lastResult);
                currentState = settledState;
                slotView.ShowWinLineAnimation(lastResult.winLines, OnWinAnimationComplete);
            }
        }
        else
        {
            uiManager.OnSpinStopping(lastResult);
            currentState = settledState;

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

        // The initial trigger — a paid base spin that awarded spins. Its scatter sequence runs
        // before the round is entered.
        if (FreeGamesEnabled && lastResult != null && lastResult.freeGame != null
            && lastResult.freeGame.spinsAwarded && !lastResult.freeGame.isFreeGame)
        {
            yield return StartCoroutine(DelayScatterTriggerResult());
            yield break;
        }

        // A retrigger — same scatter sequence, then the counter's total climbs to its new figure.
        // No prompt and no Start button; the round simply carries on.
        if (isInFreeSpins && retriggerTotalBefore >= 0)
        {
            yield return StartCoroutine(PlayRetriggerSequence());
        }

        ResumeAfterSpecialFeature();
    }

    private IEnumerator PlayRetriggerSequence()
    {
        int fromTotal = retriggerTotalBefore;
        retriggerTotalBefore = -1;


        AudioManager.Instance?.PlayScatterTrigger();
        if (slotView != null) slotView.AnimateAllScatters(scatterTriggerLoops);

        yield return new WaitForSeconds(scatterTriggerHold);

        if (freeGameView == null) yield break;

        bool countUpDone = false;
        freeGameView.AnimateTotalTo(freeSpinsRemaining, fromTotal, FreeSpinsTotalAwarded, () => countUpDone = true);
        yield return new WaitUntil(() => countUpDone);
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

    private IEnumerator DelayScatterTriggerResult()
    {
        // Nothing is pressable during the hold — PresentSpinOutcome kept the controller out of
        // Idle for it — so the button should not look pressable either. A trigger that paid a line
        // has just had its controls re-enabled by the win path. ProcessSpinResult below brings them
        // back, and StartFreeSpins then puts Start up in the same frame.
        uiManager.DisableControlsDuringWinAnimation();

        // Play special feature trigger sound AFTER all reels have stopped
        AudioManager.Instance?.PlayScatterTrigger();

        // Animate the scatters indefinitely (0 = no self-stop) so they keep playing behind the
        // award prompt while the player decides to press Start. The first free spin's StartSpin
        // stops them.
        slotView.AnimateAllScatters(0);

        // Wait for scatter hit animations to play
        yield return new WaitForSeconds(scatterTriggerHold);
        ProcessSpinResult();
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
            int totalBefore = FreeSpinsTotalAwarded;

            freeSpinsUsed++;
            freeSpinsRemaining = result.freeGame.spinsRemaining;
            freeSpinsRoundWin = result.freeGame.roundWin;

            // A retrigger animates the total up to its new figure; an ordinary spin just sets the
            // counter. The retrigger's own count-up is started later, once the scatters have
            // animated — this only records what it will count from.
            if (result.freeGame.spinsAwarded)
            {
                retriggerTotalBefore = totalBefore;
            }
            else if (freeGameView != null)
            {
                freeGameView.UpdateCounter(freeSpinsRemaining, FreeSpinsTotalAwarded);
            }
        }
    }

    // A trigger is a spin that awarded spins while not itself being a free spin — the awarding
    // spin is an ordinary paid base spin. A retrigger has spinsAwarded set too, but with
    // isFreeGame true, and needs nothing: the extra spins are already in spinsRemaining.
    //
    // One test for both places that care — PresentSpinOutcome holding the controller out of Idle
    // and ProcessSpinResult entering the round — so the hold can never outlast the entry.
    private bool IsFreeGamesTrigger(SpinResult result)
    {
        return FreeGamesEnabled && !isInFreeSpins && result != null && result.freeGame != null
            && result.freeGame.spinsAwarded && !result.freeGame.isFreeGame;
    }

    private void ProcessSpinResult()
    {
        playerData = lastResult.playerData;

        uiManager.OnSpinCompleted(lastResult);

        // This is the single place a round is entered or advanced. A feature added here should end
        // its round on the server's own "still active" flag, never on a spins-remaining counter
        // reaching zero — a round can close on the very spin that reset its counter, so the two do
        // not agree.
        if (IsFreeGamesTrigger(lastResult))
        {
            StartFreeSpins(lastResult.freeGame.spinsRemaining);
            lastResult = null;
            return;
        }

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
        wasAutoPlayingBeforeFreeSpins = false;

        uiManager.OnAutoPlayStarted();
        RequestSpin();
    }

    internal void StopAutoPlay()
    {
        isAutoPlaying = false;
        autoPlayRemainingRounds = 0;
        wasAutoPlayingBeforeFreeSpins = false;

        uiManager.OnAutoPlayStopped();

        // Autoplay skips the per-line cycle while it runs, so the round it just finished is parked
        // after Phase 1. Now that no further spin is coming, present it the way a manual spin would.
        // Covers both endings: the last scheduled round, and the player stopping part-way.
        //
        // Feature rounds are excluded. A round that parks autoplay before it begins may well have
        // paid a line on its triggering spin, and cycling those lines here would run them underneath
        // the feature intro for its whole duration. The same goes for a Free Games trigger still
        // holding for its Scatters (ShowingWin): the cycle would tear the Scatter celebration down
        // and then loop under the Start prompt.
        if (!isInFreeSpins && currentState != GameState.ShowingWin && slotView != null)
        {
            slotView.PlayWinLineCycle();
        }
    }

    internal bool ShouldResumeAutoPlay()
    {
        return wasAutoPlayingBeforeFreeSpins && (savedAutoPlayTotalRounds == -1 || savedAutoPlayRemainingRounds > 0);
    }

    internal void ResumeAutoPlay()
    {
        if (!ShouldResumeAutoPlay()) return;

        int remaining = savedAutoPlayRemainingRounds;
        int total = savedAutoPlayTotalRounds;
        wasAutoPlayingBeforeFreeSpins = false;

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

    #region Free Spins

    // Entered from a base spin that awarded spins. There is no pick and no player choice over the
    // prize — Golden Dynasty awards a flat count on 3+ scatters — but the player does choose when
    // the round begins, via the Start button that replaces Spin.
    private void StartFreeSpins(int spins)
    {
        isInFreeSpins = true;
        freeSpinsRemaining = spins;
        freeSpinsUsed = 0;
        freeSpinsRoundWin = 0;
        retriggerTotalBefore = -1;

        AudioManager.Instance?.PlayFreeSpinBg();

        int prevTotal = autoPlayTotalRounds;
        int prevRemaining = autoPlayRemainingRounds;

        if (isAutoPlaying)
        {
            StopAutoPlay();
            wasAutoPlayingBeforeFreeSpins = true;
            savedAutoPlayTotalRounds = prevTotal;
            savedAutoPlayRemainingRounds = (prevTotal != -1) ? (prevRemaining - 1) : -1;
        }

        // The prompt pulses until the player acts; the scatters keep animating underneath it,
        // started by DelayScatterTriggerResult and stopped by the first free spin.
        if (freeGameView != null) freeGameView.ShowAwardPrompt();

        uiManager.SetFreeGamesButtonLock(true);
        uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.FreeGamesStart);

        currentState = GameState.Idle;
    }

    // The Start button — routed here by UIManager's FreeGamesStart mode. The prompt becomes the
    // counter, the total counts up from 0, and the first spin follows.
    internal void StartFirstFreeSpin()
    {
        // Off FreeGamesStart and onto plain Spin, disabled. Two reasons: the round should show the
        // ordinary Spin button greyed out rather than a Start button that has already been pressed,
        // and FreeGamesStart is an "explicit" mode that SetSpinStopButtonStates refuses to touch —
        // so leaving it set would freeze the button's art for the whole round.
        uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.Spin, interactable: false);

        if (freeGameView == null)
        {
            StartCoroutine(DelayBeforeFirstFreeSpin());
            return;
        }

        freeGameView.PlayCounterIntro(FreeSpinsTotalAwarded, () => StartCoroutine(DelayBeforeFirstFreeSpin()));
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

    private void EndFreeSpins()
    {
        double roundWin = freeSpinsRoundWin;

        isInFreeSpins = false;
        freeSpinsRemaining = 0;
        AudioManager.Instance?.PlayMainBg();

        // Free spins skip the per-line cycle, so the final spin is parked after Phase 1. Start it
        // here so it plays on the reels beneath the closing summary rather than making the player
        // wait for it afterwards. Must come after isInFreeSpins is cleared — PlayWinLineCycle is a
        // no-op during free spins.
        if (slotView != null) slotView.PlayWinLineCycle();

        if (freeGameView != null)
        {
            freeGameView.PlayOutroSequence(roundWin, OnFreeGamesCountUpComplete, OnFreeGamesOutroComplete);
        }
        else
        {
            OnFreeGamesOutroComplete();
        }
    }

    // The summary's total has finished counting up — Take becomes pressable. FreeGameView owns
    // what happens on the press and calls back through OnFreeGamesOutroComplete.
    private void OnFreeGamesCountUpComplete()
    {
        uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.FreeGamesTake);
    }

    // Player took the win and the closing fade finished — restore the base game.
    private void OnFreeGamesOutroComplete()
    {
        freeSpinsUsed = 0;
        freeSpinsRoundWin = 0;
        retriggerTotalBefore = -1;

        uiManager.SetSpinButtonMode(UIManager.SpinButtonMode.Spin);
        uiManager.SetFreeGamesButtonLock(false);

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

        wasAutoPlayingBeforeFreeSpins = false;
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