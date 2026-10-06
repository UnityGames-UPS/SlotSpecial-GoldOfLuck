using UnityEngine;

public class AudioManager : MonoBehaviour
{
    internal static AudioManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        _musicEnabled = PlayerPrefs.GetInt(PrefKeyMusic, 1) == 1;
        _sfxEnabled   = PlayerPrefs.GetInt(PrefKeysfx,   1) == 1;
        _musicVolume  = PlayerPrefs.GetFloat(PrefKeyMusicVol, 0.5f);
        _sfxVolume    = PlayerPrefs.GetFloat(PrefKeySfxVol,   1.0f);

        ApplyMusicVolume();
        ApplySfxVolume();
    }

    private const string PrefKeyMusic    = "audio_music_enabled";
    private const string PrefKeysfx      = "audio_sfx_enabled";
    private const string PrefKeyMusicVol = "audio_music_volume";
    private const string PrefKeySfxVol   = "audio_sfx_volume";

    [Header("Audio Sources")]
    [SerializeField] private AudioSource bgMusicSource;
    [SerializeField] private AudioSource uiSource;
    [SerializeField] private AudioSource wheelSegmentSource;
    [SerializeField] private AudioSource reserveSource;
    [Tooltip("Dedicated source for the spin loop. Needs its own source because the loop is stopped " +
             "at landing, and stopping a shared source would cut the reel-stop one-shots firing at " +
             "that same moment.")]
    [SerializeField] private AudioSource spinLoopSource;
    [Tooltip("Dedicated source for the scatter-anticipation tension. It is stopped the moment the last " +
             "held reel lands, which is exactly when the reel-stop one-shots fire — so it cannot share " +
             "their source, and the spin loop's source is still busy with the reels at that point.")]
    [SerializeField] private AudioSource anticipationSource;

    [Header("Audio Clips")]
    [SerializeField] private AudioClip clipGameMainBg;
    [SerializeField] private AudioClip clipBetPlusMinus;
    [SerializeField] private AudioClip clipMaxBetReached;
    [SerializeField] private AudioClip clipScatterTrigger;
    [SerializeField] private AudioClip clipBigWin;
    [Tooltip("Spin button only. Stop / Take / AutoplayStop share clipPrimaryActionButton below.")]
    [SerializeField] private AudioClip clipSpinStart;
    [SerializeField] private AudioClip clipPrimaryActionButton;
    [SerializeField] private AudioClip clipGeneralButtonClick;
    [SerializeField] private AudioClip clipPopupOpenClose;
    [SerializeField] private AudioClip clipAutoplayPanelOpen;
    [SerializeField] private AudioClip clipWinPresentationStart;
    [SerializeField] private AudioClip clipReelStop;
    [Tooltip("One shot per reel that lands at least one Scatter. Currently UNASSIGNED and silent - kept because the call site is guarded and may be wanted again.")]
    [SerializeField] private AudioClip clipScatterLand;
    [SerializeField] private AudioClip clipTurboButton;

    [Header("Audio Clips - Golden Dynasty")]
    [Tooltip("The free-games congratulations panel opening.")]
    [SerializeField] private AudioClip clipCongratulations;

    [Tooltip("Phase 2 of the win presentation moving to the next win line. Fires on every change, and Phase 2 cycles until the player spins.")]
    [SerializeField] private AudioClip clipWinLineChange;

    [Tooltip("A Wild ANIMATING as part of a win — once per spin, in Phase 1 only. Wild landings have no cue.")]
    [SerializeField] private AudioClip clipWildAnimate;

    [Tooltip("The win amount counting up in the universal win popup.")]
    [SerializeField] private AudioClip clipWinCountUp;

    [Header("Audio Clips - Gold of Luck")]
    [Tooltip("The Genie Wheel's Winner panel opening — after a cash landing, and at the end of the free-games round.")]
    [SerializeField] private AudioClip clipWinner;

    [Tooltip("Start pressed: the smoke sweeps over the wheel and the wedges light up.")]
    [SerializeField] private AudioClip clipWheelSweep;

    [Tooltip("The wheel's result spin. Cut the moment the wheel lands, so it can be longer than the spin.")]
    [SerializeField] private AudioClip clipWheelSpin;

    [Tooltip("The wheel landing on its slice, with the winning-wedge sparkle.")]
    [SerializeField] private AudioClip clipWheelWin;

    [Tooltip("The free-games congratulations panel closing.")]
    [SerializeField] private AudioClip clipCongratsClose;

    [Tooltip("Scatter anticipation: plays while reels are held after two Lamps have landed, and is cut when the last held reel lands. Needs anticipationSource.")]
    [SerializeField] private AudioClip clipAnticipation;

    private bool _musicEnabled = true;
    private bool _sfxEnabled   = true;
    private float _musicVolume = 0.5f;
    private float _sfxVolume   = 1.0f;

    internal float MusicVolume => _musicVolume;
    internal float SfxVolume   => _sfxVolume;

    internal void SetMusicVolume(float volume)
    {
        _musicVolume = Mathf.Clamp01(volume);
        PlayerPrefs.SetFloat(PrefKeyMusicVol, _musicVolume);
        PlayerPrefs.Save();
        ApplyMusicVolume();
    }

    internal void SetSfxVolume(float volume)
    {
        _sfxVolume = Mathf.Clamp01(volume);
        PlayerPrefs.SetFloat(PrefKeySfxVol, _sfxVolume);
        PlayerPrefs.Save();
        ApplySfxVolume();
    }

    private void ApplyMusicVolume()
    {
        if (bgMusicSource == null) return;
        bgMusicSource.volume = _musicEnabled ? _musicVolume : 0f;
    }

    private void ApplySfxVolume()
    {
        float v = _sfxEnabled ? _sfxVolume : 0f;
        if (uiSource           != null) uiSource.volume           = v;
        if (wheelSegmentSource != null) wheelSegmentSource.volume = v;
        if (reserveSource      != null) reserveSource.volume      = v;
        if (spinLoopSource     != null) spinLoopSource.volume     = v;
        if (anticipationSource != null) anticipationSource.volume = v;
    }

    /// <summary>
    /// Uses UI source (AudioSource 2). If busy/playing, falls back to reserve source (AudioSource 4).
    /// </summary>
    private void PlayUISound(AudioClip clip)
    {
        if (!_sfxEnabled || clip == null) return;

        if (uiSource != null && !uiSource.isPlaying)
        {
            uiSource.PlayOneShot(clip);
        }
        else if (reserveSource != null)
        {
            reserveSource.PlayOneShot(clip);
        }
        else if (uiSource != null)
        {
            uiSource.PlayOneShot(clip);
        }
    }

    // A looping EFFECT, so it follows the sfx toggle and slider — muting sfx must silence it. The
    // one music bed (PlayBgMusic) sets its own volume from the music slider instead; an effect played
    // that way stays stuck at music volume, which is how the big-win and bonus-trigger sounds once
    // ended up ignoring the sfx setting.
    private void PlaySfxLoop(AudioSource source, AudioClip clip)
    {
        if (source == null || clip == null) return;
        source.clip   = clip;
        source.loop   = true;
        source.volume = _sfxEnabled ? _sfxVolume : 0f;
        source.Play();
    }

    private void StopSource(AudioSource source)
    {
        if (source == null) return;
        source.Stop();
        source.loop = false;
    }

    // 1. Game Main BG
    internal void PlayBgMusic()
    {
        if (bgMusicSource == null || clipGameMainBg == null) return;
        if (bgMusicSource.isPlaying && bgMusicSource.clip == clipGameMainBg) return;

        bgMusicSource.clip   = clipGameMainBg;
        bgMusicSource.loop   = true;
        bgMusicSource.volume = _musicEnabled ? _musicVolume : 0f;
        bgMusicSource.Play();
    }

    internal void StopBgMusic()
    {
        StopSource(bgMusicSource);
    }

    // 2. Bet Plus / Bet Minus (one for both)
    internal void PlayBetPlusMinus()
    {
        PlayUISound(clipBetPlusMinus);
    }

    // 3. Max Bet Reached
    internal void PlayMaxBetReached()
    {
        PlayUISound(clipMaxBetReached);
    }

    // 4. Bonus-trigger stinger — a one-shot, despite the CNY-era "Loop" in the name. It used to go
    // through PlayLoop, which sets loop = true, and the matching Stop method had no callers — so the
    // clip repeated for the rest of the session from the moment free games triggered. PlayUISound
    // already null-guards and honours _sfxEnabled, so no guard is needed here.
    internal void PlayScatterTrigger()
    {
        PlayUISound(clipScatterTrigger);
    }

    // 5. Win Object BG (Play at Open)
    internal void PlayBigWin()
    {
        if (!_sfxEnabled || clipBigWin == null) return;
        // PlaySfxLoop, not PlayLoop: this is an effect, not a music bed. PlayLoop stamps the source
        // with the *music* volume and StopSource never restores it, so every later UI sound on
        // uiSource kept playing at music level until something touched a volume slider.
        PlaySfxLoop(uiSource, clipBigWin);
    }

    internal void StopBigWin()
    {
        if (uiSource != null && uiSource.clip == clipBigWin)
        {
            StopSource(uiSource);
        }
        if (reserveSource != null && reserveSource.clip == clipBigWin)
        {
            StopSource(reserveSource);
        }
    }

    // 6. Stop / Take / AutoplayStop / WheelStart Btn Sound
    internal void PlayPrimaryActionButton()
    {
        PlayUISound(clipPrimaryActionButton != null ? clipPrimaryActionButton : clipGeneralButtonClick);
    }

    // Spin is a *duration* sound, not a button click: it loops for as long as the reels turn and is
    // cut by StopSpinLoop at landing. Played as a one-shot it ran on past the landing (the clip is
    // several seconds long) and stacked a fresh copy on every autoplay spin, since PlayOneShot never
    // cancels the previous one.
    internal void PlaySpinStart()
    {
        if (!_sfxEnabled) return;

        if (clipSpinStart != null)
        {
            PlaySfxLoop(spinLoopSource, clipSpinStart);
        }
        else
        {
            // No spin clip assigned: fall back to the shared primary-action click as a one-shot.
            // Looping a button click would be worse than the missing sound it stands in for.
            PlayUISound(clipPrimaryActionButton);
        }
    }

    // Safe to call when nothing is playing, which is what lets the two call sites in the reel-stop
    // path both fire without coordinating.
    internal void StopSpinLoop()
    {
        StopSource(spinLoopSource);
    }

    internal void PlaySpinStop()     => PlayPrimaryActionButton();
    internal void PlayTakeButton()   => PlayPrimaryActionButton();
    internal void PlayAutoplayStop() => PlayPrimaryActionButton();

    // 7. General Button Click
    internal void PlayButton()
    {
        PlayUISound(clipGeneralButtonClick);
    }

    // 8. Popup Open Close Sound
    internal void PlayPopupOpenClose()
    {
        PlayUISound(clipPopupOpenClose != null ? clipPopupOpenClose : clipGeneralButtonClick);
    }

    internal void PlayPopupClose() => PlayPopupOpenClose();

    // 9. Autoplay Panel Open Sound
    internal void PlayAutoplayPanelOpen()
    {
        PlayUISound(clipAutoplayPanelOpen != null ? clipAutoplayPanelOpen : clipPopupOpenClose);
    }

    // 13. Win Line Phase 1 Start
    internal void PlayWinPresentationStart()
    {
        PlayUISound(clipWinPresentationStart);
    }

    // 14. Slot Reel Column Stop Sound
    internal void PlayReelStop()
    {
        if (!_sfxEnabled || clipReelStop == null) return;

        if (wheelSegmentSource != null)
            wheelSegmentSource.PlayOneShot(clipReelStop);
        else
            PlayUISound(clipReelStop);
    }

    // 15. Scatter landing — one shot per reel that contains at least one, not per symbol. Wild
    // landings deliberately have NO cue in this game: the Wild is announced when it animates.
    internal void PlayScatterLand() => PlayUISound(clipScatterLand);

    // 16. Turbo / spin-speed toggle
    internal void PlayTurboButton() => PlayUISound(clipTurboButton);

    // 17. Golden Dynasty cues.
    internal void PlayWinLineChange()      => PlayUISound(clipWinLineChange);
    internal void PlayWildAnimate()        => PlayUISound(clipWildAnimate);
    internal void PlayWinCountUp()         => PlayUISound(clipWinCountUp);
    internal void PlayCongratulations() => PlayUISound(clipCongratulations);

    // 18. Gold of Luck cues.
    internal void PlayWinner()     => PlayUISound(clipWinner);
    internal void PlayWheelSweep() => PlayUISound(clipWheelSweep);
    internal void PlayWheelWin()   => PlayUISound(clipWheelWin);

    // The wheel spin is a DURATION sound, like the reel spin: it has to stop the instant the wheel
    // lands, which a one-shot cannot do. It borrows spinLoopSource, the reel spin's own source — the
    // two can never overlap, because the reels are always at rest while the wheel spins. Not looped:
    // the clip is longer than the spin and is cut at landing, so a loop would only ever be heard if
    // the spin were tuned longer than the clip.
    internal void PlayWheelSpin()
    {
        if (!_sfxEnabled || spinLoopSource == null || clipWheelSpin == null) return;

        spinLoopSource.clip   = clipWheelSpin;
        spinLoopSource.loop   = false;
        spinLoopSource.volume = _sfxVolume;
        spinLoopSource.Play();
    }

    // Safe to call when nothing is playing.
    internal void StopWheelSpin()
    {
        StopSource(spinLoopSource);
    }

    internal void PlayCongratsClose() => PlayUISound(clipCongratsClose);

    // Scatter anticipation is a DURATION sound: it runs while reels are held and stops when the last
    // held reel lands. Several reels can be held in one spin, so it is started once and left running
    // across them — a second call while it plays does nothing rather than restarting it. Not looped:
    // the clip outlasts any hold.
    private bool anticipationSourceWarned;
    internal void PlayAnticipation()
    {
        if (!_sfxEnabled || clipAnticipation == null) return;

        if (anticipationSource == null)
        {
            if (!anticipationSourceWarned)
            {
                anticipationSourceWarned = true;
                Debug.LogWarning("[AudioManager] anticipationSource is not wired — the scatter anticipation sound is silent.");
            }
            return;
        }

        if (anticipationSource.isPlaying) return;

        anticipationSource.clip   = clipAnticipation;
        anticipationSource.loop   = false;
        anticipationSource.volume = _sfxVolume;
        anticipationSource.Play();
    }

    // Safe to call when nothing is playing.
    internal void StopAnticipation()
    {
        StopSource(anticipationSource);
    }

    private bool isForceMuted = false;

    internal void SetMuteAll(bool forceMute)
    {
        if (forceMute == isForceMuted) return;
        isForceMuted = forceMute;

        AudioListener.volume = forceMute ? 0f : 1f;
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        SetMuteAll(!hasFocus);
    }

    private void OnApplicationPause(bool isPaused)
    {
        SetMuteAll(isPaused);
    }
}
