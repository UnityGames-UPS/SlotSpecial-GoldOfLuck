using System;
using System.Collections.Generic;

#region Server Communication Models — Init

[Serializable]
public class InitData
{
    public string id = "initData";
    public ServerGameData gameData;
    public ServerFeatures features;
    public ServerUIData uiData;
    public ServerPlayer player;
    // Gold of Luck sends no jackpot block. Kept because the platform's separate "jackpot:sync"
    // event reuses these types, and the init-time read is already null-guarded.
    public JackpotData jackpotData;
}

[Serializable]
public class JackpotData
{
    public JackpotValues values;
}

[Serializable]
public class JackpotValues
{
    public string miniJackpot;
    public string minorJackpot;
    public string majorJackpot;
    public string grandJackpot;
}

[Serializable]
public class JackpotSyncData
{
    public string gameId;
    public JackpotValues values;
}

// The four platform jackpot tiers, in the order the platform ranks them.
public enum JackpotTier
{
    Grand,
    Major,
    Minor,
    Mini
}

// Clicking a jackpot tier asks the platform to open its own jackpot overlay. Fire-and-forget —
// the server sends no direct reply; any resulting value change arrives on the usual jackpot:sync.
[Serializable]
public class JackpotOpenRequest
{
    public string type = "JACKPOT_OPEN";
    public JackpotOpenPayload payload = new JackpotOpenPayload();
}

[Serializable]
public class JackpotOpenPayload
{
    public string tier;
}

[Serializable]
public class ServerGameData
{
    // Confirmed against a live init. "lines" is a leftover of the payline backends and arrives
    // EMPTY; it is read only for the reel count, which falls back to 5.
    public List<List<int>> lines;
    public List<double> bets;
    // Total bet = the selected bet x this: 0.01 x 50 = 0.50, the cost of every captured spin.
    public int creditDivisor;

    // The WAYS count (243). Bound for the line-count label ONLY, as GameConfig.waysCount — never a bet
    // multiplier. Read as the old "lines to multiply the bet by" it would make every bet display and
    // deduction 4.86x too high; creditDivisor is the multiplier.
    public int totalLines;
}

[Serializable]
public class ServerFeatures
{
    public ServerGenieWheelFeature genieWheel;
    public ServerGenieWildFeature genieWild;
    public ServerFreeGamesFeature freeGames;
}

[Serializable]
public class ServerGenieWheelFeature
{
    // Confirmed against a live init. The converter logs an error if it ever comes back empty, so a
    // renamed array fails loudly rather than leaving a wheel with no slices.
    public List<ServerWheelSlice> segments;

    // Sent and deliberately unbound: "enabled", "symbolId" (9, the Lamp — found through the symbol
    // table's group instead), "minTrigger" (3) and "requiredReels" ([2,3,4], zero-based, so reels 3,
    // 4 and 5). The anticipation doesn't need requiredReels: the backend only puts Lamps on those
    // reels, so counting landed Lamps gives the same answer.
}

[Serializable]
public class ServerWheelSlice
{
    public int sliceIndex;
    public string type;       // "COIN", "MULTIPLIER" or "FREE_GAMES"
    // Which are set depends on the type: a COIN slice has a coin, a FREE_GAMES slice has freeGames, and a
    // MULTIPLIER slice has BOTH a coin and a multiplier (it pays coin × multiplier × total bet). double for
    // coin in case a fractional value is ever sent; every captured coin so far has been a whole number.
    public double coin;
    public int multiplier;
    public int freeGames;
}

[Serializable]
public class ServerGenieWildFeature
{
    // The values a landed Genie can carry. The per-Genie value itself arrives with each spin, in
    // payload.genieMultipliers — this list is only for describing the feature.
    // "symbolId" and "maxMultiplierProduct" are sent too and deliberately unbound: the Genie is
    // found through the symbol table's "group", and the product cap is the server's business.
    public List<int> multipliers;
}

[Serializable]
public class ServerFreeGamesFeature
{
    // Nothing is bound. "maxTotalFreeGames" (the cap on one round, retriggers included) is sent and
    // deliberately unbound: the server enforces it, and the client only shows what each spin reports.
    // "payMultiplier" is sent (1) but appliedMultiplier reads 1 on every captured win, free games
    // included, and the win amounts arrive already multiplied. What a trigger awards comes from the
    // wheel.
}

[Serializable]
public class ServerUIData
{
    // The name is inherited from the payline backends; it holds the symbol table.
    public PaylineData paylines;
}

[Serializable]
public class PaylineData
{
    public List<ServerSymbolInfo> symbols;
}

[Serializable]
public class ServerSymbolInfo
{
    public int id;
    public string name;         // "Prince", "Genie", "Lamp", ... — display only, never used to find a role
    // "high", "low", "wild" or "scatter". This is what tells the special symbols apart; the init
    // sends no isSpecialSymbol flag and no displayName. "description" only repeats the group.
    public string group;
    // 3 on every symbol, wild and scatter included, where it means nothing.
    public int minMatch;
    // ASCENDING from a minMatch match: index 0 = 3-of-a-kind, 1 = 4, 2 = 5. Credits, not cash — one
    // credit is the selected bet. Empty on Genie and Lamp. The converter reverses it, because the
    // client works descending from a full-reel match.
    public List<double> payout;
}

[Serializable]
public class ServerPlayer
{
    public double balance;
}

#endregion

#region Server Communication Models — Spin Result

[Serializable]
public class ServerSpinResponse
{
    public string id = "ResultData";
    public bool success;
    // Row-major and top-level, NOT nested under payload: 3 rows x 5 columns, values as strings.
    public List<List<string>> matrix;
    public ServerPayload payload;
    public ServerPlayerBalance player;
}

[Serializable]
public class ServerPlayerBalance
{
    // Nullable in case the server ever omits it; the converter falls back to a computed balance.
    public double? balance;
}

[Serializable]
public class ServerPayload
{
    // ONLY the ways subtotal. On a spin whose Lamps triggered the wheel this is short of what was
    // paid — the wheel prize is not in it. Never display it; see grandTotalWin.
    public double winAmount;
    // One entry per winning SYMBOL, not per way — waysCount says how many ways it covers.
    public List<ServerWaysWin> waysWins;

    // Every Genie on the board, keyed "row,col" (comma-separated), value the
    // multiplier that Genie carries. Sent for every Genie whether or not it is part of a win, so it
    // is a landing-time fact, not a win-time one.
    public Dictionary<string, int> genieMultipliers;

    public ServerGenieWheelResult genieWheel;
    public ServerFreeGamesResult freeGames;

    // The spin's total: ways plus wheel prize, and the only figure to display. The balance moves by
    // exactly this minus the bet (5337.83 -> 5354.33 on a 17.00 win at a 0.50 bet), and free spins
    // deduct no bet, so there it moves by this alone.
    public double grandTotalWin;

    // "netReturnRatio" (grandTotalWin over total bet) is sent and deliberately unbound: the
    // controller already derives the same ratio from the win and the total bet.
}

[Serializable]
public class ServerWaysWin
{
    public int symbolId;
    public int matchCount;    // reels matched from the left: 3, 4 or 5
    public int waysCount;     // how many ways this symbol wins on
    // Every cell that takes part, Genies standing in for the symbol included, one entry each,
    // ordered by column then row. This is the union across all the ways, not one path.
    public List<ServerCell> matchedPositions;
    // Already inside winInCash. Kept so the view can show "x2" without dividing anything back out.
    public int genieMultiplierProduct;
    public double winInCash;

    // Sent and deliberately unbound: basePayout, winInCredits, winType ("WAYS_MATCH" is the only
    // value seen) and appliedMultiplier, which has read 1 on every captured win. winInCash is
    // basePayout x waysCount x genieMultiplierProduct, already converted, so nothing needs redoing.
}

[Serializable]
public class ServerCell
{
    public int row;
    public int col;
}

[Serializable]
public class ServerGenieWheelResult
{
    public bool triggered;
    // Absent unless triggered.
    public ServerWheelResult result;
}

[Serializable]
public class ServerWheelResult
{
    // Indexes the slice list sent at init. multiplierAwarded and freeGamesAwarded repeat that
    // slice's own figures, so the client can cross-check what it is about to draw.
    public int sliceIndex;
    public string type;
    public double coinAwarded;
    public int multiplierAwarded;
    public int freeGamesAwarded;
    // The prize in cash, already inside grandTotalWin. A MULTIPLIER slice carries a coin as well and
    // pays coin x multiplierAwarded x the TOTAL bet, even on a free spin. The client never computes
    // this figure — it always shows what the server sends here.
    public double winInCash;
}

[Serializable]
public class ServerFreeGamesResult
{
    // Spins played so far INCLUDING this one: 0 on the spin that triggers a round, 3 on the last of
    // three. After the spin, like remaining.
    public int played;
    // Spins left after this one. On the trigger spin itself, the whole award.
    public int remaining;
    // The round's running total: the sum of the FREE spins' wins. The trigger spin's own win is
    // not in it — it reads 0 there.
    public double totalFreeGamesWin;

    // Sent and deliberately unbound, because both read differently from their old namesakes:
    //  - "inFreeGames" is the state AFTER the spin. It is true on the trigger spin and false on the
    //    last free spin, itself a free game. It is not "this spin was free".
    //  - "triggered" stays true for every spin of a round, the last included, and is not a
    //    per-spin trigger flag.
    // "totalAwarded" is sent as well; the controller derives the same figure from played + remaining.
}

#endregion

#region Client-Side Spin Request

[Serializable]
public class SpinRequest
{
    public string type = "SPIN";
    public SpinPayload payload;
}

[Serializable]
public class SpinPayload
{
    // betIndex is the only field with a confirmed effect. The server owns free-spin state, so
    // there is no isFreeSpin flag to send. The mock client also sends "spins": 100; nobody knows
    // what it does, and this client deliberately does not send it.
    public int betIndex;
}

#endregion

#region Game Configuration (Client Side Converted)

[Serializable]
public class GameConfig
{
    public int reelCount = 5;
    // Every row the server sends is live and pays.
    public int rowCount = 3;

    // Total bet = selected bet x activeLine. Named for the payline games; here it is the init's
    // creditDivisor (50), and has nothing to do with the 243 ways.
    public int activeLine = 50;

    // The ways count (243), for the line-count label only — never multiply anything by it. 0 when the
    // init doesn't send it, and the label then keeps its scene text.
    public int waysCount;

    public List<double> availableBets;
    public List<SymbolInfo> symbols;

    // The Genie Wheel's slices, sorted by sliceIndex. Empty means the init carried none.
    public List<WheelSlice> wheelSlices;
    // The multipliers a Genie can carry, for describing the feature.
    public List<int> wildMultipliers;

    // Resolved from the init symbol table. -1 means "not present", so an unresolved role can never
    // collide with a real symbol id the way a 0 default would — and -1 stays meaningful downstream:
    // SocketIOManager keeps both off the pre-spin placeholder board, and reads -1 as "no such
    // symbol, leave nothing out".
    public int wildSymbolId = -1;
    public int scatterSymbolId = -1;
}

[Serializable]
public class SymbolInfo
{
    public int id;
    public string name;
    // Descending from a full-reel match: index 0 = reelCount-of-a-kind, 1 = one fewer, and so on.
    // In credits — multiply by the selected bet for cash.
    public List<double> multipliers;

    public bool isWild;
    public bool isScatter;
    // True for the Genie and the Lamp.
    public bool isSpecial;

    // Fewest matching symbols that pay. 0 when the symbol has no paytable (Genie, Lamp).
    public int minMatch;
}

public enum WheelSliceType
{
    Coin,
    Multiplier,
    FreeGames
}

[Serializable]
public class WheelSlice
{
    public int sliceIndex;
    public WheelSliceType type;
    // The coin value as the init sends it. A MULTIPLIER slice carries one too. What a landing pays
    // in cash is always the server's figure (see ServerWheelResult).
    public double coin;
    // Applied to the slice's coin: coin x multiplier x the TOTAL bet.
    public int multiplier;
    public int freeGames;
}

#endregion

#region Player & Game State (Client Side)

[Serializable]
public class PlayerData
{
    public double balance;
    public int currentBetIndex;
}

[Serializable]
public class SpinResult
{
    // Column-major: [reel][row], the transpose of the server's row-major matrix.
    public List<List<int>> resultMatrix;

    // Everything the spin paid — ways plus wheel prize — from grandTotalWin. This is the figure to
    // show and the one the balance moved by.
    public double winAmount;
    // The ways wins alone. The gap to winAmount is the wheel prize, which is presented separately.
    public double waysWinAmount;

    // One entry per winning symbol.
    public List<WinLine> winLines;
    public PlayerData playerData;

    // Always present — the server sends the free-games block on every spin, in or out of a round.
    public FreeGameData freeGame;

    // Every Genie on the board: flat index (row * reelCount + col) -> its multiplier. Empty when
    // there is none. Populated whether or not the Genie is part of a win.
    public Dictionary<int, int> genieMultipliers;

    // Always present. triggered is false on almost every spin.
    public GenieWheelData genieWheel;
}

/// <summary>
/// One winning symbol's ways win. Named for the payline games it replaced, whose one-line-at-a-time
/// presentation still consumes it.
/// </summary>
[Serializable]
public class WinLine
{
    // Position in the response's waysWins, not a payline number — there are no paylines.
    public int lineId;
    public int symbolId;
    // Flat indices into the active grid: row * reelCount + col. Every cell in the win.
    public List<int> positions;
    public double winAmount;

    // Reels matched from the left, and how many ways the symbol wins on.
    public int matchCount;
    public int waysCount;
    // Product of the Genies in the win, already inside winAmount. 1 when there is none.
    public int multiplier;
}

/// <summary>
/// The free-games facts for one spin, mapped from payload.freeGames onto the meanings the
/// controller was built around. The mapping lives in the converter so the controller never sees
/// the wire's post-spin flags — see ServerFreeGamesResult.
/// </summary>
[Serializable]
public class FreeGameData
{
    // Spins left AFTER this one. Retriggers are folded in, so it can go up as well as down.
    public int spinsRemaining;

    // Set on any spin whose wheel landed on a free-games slice. The controller only reads it inside a
    // round, where it marks a retrigger; the initial award is handled by the Genie Wheel itself.
    public bool spinsAwarded;

    // The round's running total, server-authoritative. Excludes the trigger spin's own win.
    public double roundWin;
}

/// <summary>
/// What the Genie Wheel did on one spin. The landing slice is a server fact: the view animates to
/// it and shows what it is given, never picking a slice or deriving a prize from an angle.
/// </summary>
[Serializable]
public class GenieWheelData
{
    // True on the spin whose Lamps triggered the wheel — in the base game or on a free spin.
    public bool triggered;

    // Indexes GameConfig.wheelSlices.
    public int sliceIndex;
    public WheelSliceType type;
    // Spins awarded — set on a FREE_GAMES landing only.
    public int freeGames;
    // The cash prize, already inside SpinResult.winAmount. Always the server's figure — show this,
    // never a value derived from the slice's coin or multiplier. (The result's coinAwarded and
    // multiplierAwarded are read only to work out the type when the type word is unrecognised.)
    public double winAmount;
}

#endregion

#region Enums

public enum GameState
{
    Initializing,
    Idle,
    Spinning,
    Stopping,
    ShowingWin
}

public enum SpinSpeed
{
    Normal,
    Turbo,
    QuickSpin
}

public enum WinPopupType
{
    BigWin
}

#endregion

#region Helper Classes for Conversion

/// <summary>
/// The single seam between server JSON and client types. Wire shapes change here and nowhere else,
/// so the view and controller layers never see a backend revision.
/// </summary>
public static class InitDataConverter
{
    // Used only if the init omits creditDivisor. Total bet = selected bet x 50, which matches every
    // captured balance: each non-winning spin at bet 0.01 cost exactly 0.50.
    private const int FallbackBetMultiplier = 50;

    internal static GameConfig ConvertToGameConfig(InitData serverData)
    {
        var gameData = serverData?.gameData;
        var serverSymbols = serverData?.uiData?.paylines?.symbols;

        int reelCount = (gameData?.lines != null && gameData.lines.Count > 0 && gameData.lines[0] != null)
            ? gameData.lines[0].Count
            : 5;

        int betMultiplier = gameData != null && gameData.creditDivisor > 0 ? gameData.creditDivisor : FallbackBetMultiplier;
        if (gameData == null || gameData.creditDivisor <= 0)
        {
            UnityEngine.Debug.LogError($"[InitDataConverter] Init carried no creditDivisor — total bet assumed to be bet x {FallbackBetMultiplier}.");
        }

        var config = new GameConfig
        {
            reelCount = reelCount,
            rowCount = 3,
            activeLine = betMultiplier,
            waysCount = gameData != null && gameData.totalLines > 0 ? gameData.totalLines : 0,
            availableBets = gameData?.bets,
            symbols = new List<SymbolInfo>(),
            wheelSlices = ConvertWheelSlices(serverData?.features?.genieWheel),
            wildMultipliers = serverData?.features?.genieWild?.multipliers ?? new List<int>()
        };

        if (config.availableBets == null || config.availableBets.Count == 0)
        {
            UnityEngine.Debug.LogError("[InitDataConverter] Init carried no bet levels — the bet controls will do nothing.");
        }

        if (serverSymbols == null)
        {
            UnityEngine.Debug.LogError("[InitDataConverter] Init carried no symbol table — symbol art and paytables will be unresolved.");
            return config;
        }

        foreach (var serverSymbol in serverSymbols)
        {
            if (serverSymbol == null) continue;

            // The role comes from "group", never the name.
            string group = (serverSymbol.group ?? string.Empty).Trim().ToLowerInvariant();

            var payout = serverSymbol.payout ?? new List<double>();

            // The wire runs 3-of-a-kind first; the client works descending from a full-reel match,
            // so reversing here leaves every consumer of multipliers as it was.
            var multipliers = new List<double>(payout);
            multipliers.Reverse();

            var symbolInfo = new SymbolInfo
            {
                id = serverSymbol.id,
                name = serverSymbol.name,
                multipliers = multipliers,
                isWild = group == "wild",
                isScatter = group == "scatter",
                minMatch = payout.Count > 0 ? serverSymbol.minMatch : 0
            };
            symbolInfo.isSpecial = symbolInfo.isWild || symbolInfo.isScatter;

            // An unknown group means the backend added a role this client has never heard of.
            if (group != "high" && group != "low" && !symbolInfo.isSpecial)
            {
                UnityEngine.Debug.LogError($"[InitDataConverter] Symbol id {symbolInfo.id} (name '{symbolInfo.name}') has unrecognised group '{serverSymbol.group}' — it will be treated as an ordinary symbol.");
            }

            config.symbols.Add(symbolInfo);

            if (symbolInfo.isWild) config.wildSymbolId = symbolInfo.id;
            if (symbolInfo.isScatter) config.scatterSymbolId = symbolInfo.id;
        }

        return config;
    }

    // Sorted by sliceIndex so a slice's place in the list is its index, whatever order the server
    // sent them in. Empty and loud when the init carried none: the wheel cannot be drawn without them.
    private static List<WheelSlice> ConvertWheelSlices(ServerGenieWheelFeature wheel)
    {
        var slices = new List<WheelSlice>();

        if (wheel?.segments == null || wheel.segments.Count == 0)
        {
            UnityEngine.Debug.LogError("[InitDataConverter] Init carried no Genie Wheel slices — the wheel cannot be drawn. Check the array's name in features.genieWheel.");
            return slices;
        }

        foreach (var serverSlice in wheel.segments)
        {
            if (serverSlice == null) continue;

            slices.Add(new WheelSlice
            {
                sliceIndex = serverSlice.sliceIndex,
                type = ParseSliceType(serverSlice.type, serverSlice.coin, serverSlice.multiplier, serverSlice.freeGames, "wheel slice " + serverSlice.sliceIndex),
                coin = serverSlice.coin,
                multiplier = serverSlice.multiplier,
                freeGames = serverSlice.freeGames
            });
        }

        slices.Sort((a, b) => a.sliceIndex.CompareTo(b.sliceIndex));
        return slices;
    }

    // An unknown type is read from its figures, and logged — the backend has added a slice kind this
    // client doesn't know. The amount paid is unaffected either way, since the client always shows the
    // server's winInCash; only how the wheel describes the slice would be wrong.
    //
    // The multiplier is checked BEFORE the coin: a MULTIPLIER slice carries a coin value too, so a coin
    // check first would misread it as a COIN slice and lose its badge.
    private static WheelSliceType ParseSliceType(string type, double coin, int multiplier, int freeGames, string source)
    {
        switch ((type ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "COIN":       return WheelSliceType.Coin;
            case "MULTIPLIER": return WheelSliceType.Multiplier;
            case "FREE_GAMES": return WheelSliceType.FreeGames;
        }

        WheelSliceType guess = freeGames > 0  ? WheelSliceType.FreeGames
                             : multiplier > 0 ? WheelSliceType.Multiplier
                             : coin > 0       ? WheelSliceType.Coin
                             : WheelSliceType.Multiplier;
        UnityEngine.Debug.LogError($"[InitDataConverter] {source} has unrecognised type '{type}' — read as {guess} from its figures.");
        return guess;
    }

    internal static PlayerData ConvertToPlayerData(ServerPlayer serverPlayer, int defaultBetIndex = 0)
    {
        return new PlayerData
        {
            balance = serverPlayer != null ? serverPlayer.balance : 0,
            currentBetIndex = defaultBetIndex
        };
    }

    /// <summary>
    /// Converts one spin response into the client's SpinResult.
    /// betAmount is accepted for signature stability but no longer used: the server always sends
    /// the post-spin balance, and the fallback below works off the already-deducted local balance.
    /// </summary>
    internal static SpinResult ConvertServerResponseToSpinResult(ServerSpinResponse serverResponse, double currentBalance, double betAmount, GameConfig gameConfig)
    {
        var payload = serverResponse?.payload;

        // grandTotalWin is the spin's total — ways and wheel prize together — and passes straight
        // through. payload.winAmount is the ways subtotal only, so it is kept separately and never
        // used as the figure to show. Nothing is summed here.
        double totalWin = payload?.grandTotalWin ?? 0;
        double newBalance = serverResponse?.player?.balance ?? CalculateNewBalance(currentBalance, totalWin);

        return new SpinResult
        {
            resultMatrix = ConvertMatrixToColumns(serverResponse?.matrix, gameConfig),
            winAmount = totalWin,
            waysWinAmount = payload?.winAmount ?? 0,
            winLines = ConvertWaysWins(payload?.waysWins, gameConfig),

            playerData = new PlayerData
            {
                balance = newBalance,
                currentBetIndex = 0
            },

            freeGame = ConvertFreeGame(payload?.freeGames, payload?.genieWheel),
            genieMultipliers = ConvertGenieMultipliers(payload?.genieMultipliers, gameConfig),
            genieWheel = ConvertGenieWheel(payload?.genieWheel)
        };
    }

    // Never returns null, so the controller can read it without guarding every access. A missing
    // block is indistinguishable from "not in a round", which is the correct reading either way.
    //
    // Maps the wire's post-spin fields onto the per-spin meanings the controller was built on:
    //  - spinsAwarded is "the wheel landed on a free-games slice". The wire's freeGames.triggered
    //    stays true for the whole round, so it cannot say which spin did the awarding.
    private static FreeGameData ConvertFreeGame(ServerFreeGamesResult serverFreeGames, ServerGenieWheelResult wheel)
    {
        if (serverFreeGames == null) return new FreeGameData();

        bool wheelAwardedGames = wheel != null && wheel.triggered
            && wheel.result != null && wheel.result.freeGamesAwarded > 0;

        return new FreeGameData
        {
            spinsRemaining = serverFreeGames.remaining,
            spinsAwarded = wheelAwardedGames,
            roundWin = serverFreeGames.totalFreeGamesWin
        };
    }

    // A wheel that reports itself triggered with no result is treated as not triggered: there is
    // nothing to animate to, and pretending otherwise would stall the round waiting for a landing.
    private static GenieWheelData ConvertGenieWheel(ServerGenieWheelResult serverWheel)
    {
        var data = new GenieWheelData();

        if (serverWheel == null || !serverWheel.triggered) return data;

        if (serverWheel.result == null)
        {
            UnityEngine.Debug.LogError("[InitDataConverter] genieWheel reported triggered but carried no result — wheel skipped.");
            return data;
        }

        var result = serverWheel.result;
        data.triggered = true;
        data.sliceIndex = result.sliceIndex;
        data.type = ParseSliceType(result.type, result.coinAwarded, result.multiplierAwarded, result.freeGamesAwarded, "genieWheel result");
        data.freeGames = result.freeGamesAwarded;
        data.winAmount = result.winInCash;
        return data;
    }

    // Turns payload.genieMultipliers into flat cell index -> multiplier. One bad key is skipped
    // rather than costing the whole spin.
    private static Dictionary<int, int> ConvertGenieMultipliers(Dictionary<string, int> genieMultipliers, GameConfig gameConfig)
    {
        var result = new Dictionary<int, int>();
        if (genieMultipliers == null) return result;

        foreach (var entry in genieMultipliers)
        {
            if (TryParseCellKey(entry.Key, gameConfig, "genieMultipliers", out int flatIndex))
            {
                result[flatIndex] = entry.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// Reads a "row,col" cell key into a flat index (row * reelCount + col). Confirmed against live
    /// data: a key of "0,3" lands on a Genie in the matrix only when read as row 0, column 3.
    ///
    /// Returns false and logs for anything malformed or out of range, so one bad cell is skipped
    /// rather than costing the whole spin.
    /// </summary>
    private static bool TryParseCellKey(string key, GameConfig gameConfig, string source, out int flatIndex)
    {
        flatIndex = -1;
        if (string.IsNullOrEmpty(key)) return false;

        int separator = key.IndexOf(',');
        if (separator <= 0 || separator >= key.Length - 1)
        {
            UnityEngine.Debug.LogError($"[InitDataConverter] {source} key '{key}' is not in the expected row,col form — cell skipped.");
            return false;
        }

        if (!int.TryParse(key.Substring(0, separator), out int row)
            || !int.TryParse(key.Substring(separator + 1), out int col))
        {
            UnityEngine.Debug.LogError($"[InitDataConverter] Could not read row/col out of {source} key '{key}' — cell skipped.");
            return false;
        }

        return TryGetFlatIndex(row, col, gameConfig, $"{source} key '{key}'", out flatIndex);
    }

    // The one place a row and column become a flat index, so the bounds check cannot drift between
    // the callers that read keys and the ones that read {row, col} objects.
    private static bool TryGetFlatIndex(int row, int col, GameConfig gameConfig, string source, out int flatIndex)
    {
        flatIndex = -1;

        int reelCount = gameConfig != null ? gameConfig.reelCount : 5;
        int rowCount = gameConfig != null ? gameConfig.rowCount : 3;

        if (row < 0 || row >= rowCount || col < 0 || col >= reelCount)
        {
            UnityEngine.Debug.LogError($"[InitDataConverter] {source} is outside the {rowCount}x{reelCount} grid — cell skipped.");
            return false;
        }

        flatIndex = row * reelCount + col;
        return true;
    }

    // Server matrix is row-major (matrix[row][col]); the client works column-major ([reel][row]),
    // so this transposes. No padding rows to skip — every row the server sends is live.
    private static List<List<int>> ConvertMatrixToColumns(List<List<string>> serverMatrix, GameConfig gameConfig)
    {
        int reelCount = gameConfig != null ? gameConfig.reelCount : 5;
        int rowCount = gameConfig != null ? gameConfig.rowCount : 3;

        if (serverMatrix == null || serverMatrix.Count == 0)
        {
            UnityEngine.Debug.LogError("[InitDataConverter] Spin response carried no matrix.");
            return GenerateDefaultMatrix(reelCount, rowCount);
        }

        var matrix = new List<List<int>>();

        for (int col = 0; col < reelCount; col++)
        {
            var column = new List<int>();
            for (int row = 0; row < rowCount; row++)
            {
                if (row >= serverMatrix.Count || serverMatrix[row] == null || col >= serverMatrix[row].Count)
                {
                    UnityEngine.Debug.LogError($"[InitDataConverter] matrix has no cell at row {row}, col {col}.");
                    column.Add(0);
                    continue;
                }

                if (!int.TryParse(serverMatrix[row][col], out int symbolId))
                {
                    UnityEngine.Debug.LogError($"[InitDataConverter] Could not parse symbol at row {row}, col {col}.");
                    symbolId = 0;
                }

                column.Add(symbolId);
            }
            matrix.Add(column);
        }

        return matrix;
    }

    // One WinLine per winning symbol. The server already says which cells take part and which symbol
    // paid, so unlike the payline games nothing is rebuilt from a line table.
    private static List<WinLine> ConvertWaysWins(List<ServerWaysWin> waysWins, GameConfig gameConfig)
    {
        var winLines = new List<WinLine>();
        if (waysWins == null) return winLines;

        for (int i = 0; i < waysWins.Count; i++)
        {
            var waysWin = waysWins[i];
            if (waysWin == null) continue;

            var flatPositions = new List<int>();
            if (waysWin.matchedPositions != null)
            {
                foreach (var cell in waysWin.matchedPositions)
                {
                    if (cell == null) continue;

                    if (TryGetFlatIndex(cell.row, cell.col, gameConfig, $"waysWins[{i}] cell ({cell.row},{cell.col})", out int flatIndex))
                    {
                        flatPositions.Add(flatIndex);
                    }
                }
            }

            winLines.Add(new WinLine
            {
                lineId = i,
                symbolId = waysWin.symbolId,
                positions = flatPositions,
                winAmount = waysWin.winInCash,
                matchCount = waysWin.matchCount,
                waysCount = waysWin.waysCount,
                multiplier = Math.Max(1, waysWin.genieMultiplierProduct)
            });
        }

        return winLines;
    }

    private static List<List<int>> GenerateDefaultMatrix(int reelCount, int rowCount)
    {
        var matrix = new List<List<int>>();
        for (int col = 0; col < reelCount; col++)
        {
            var column = new List<int>();
            for (int row = 0; row < rowCount; row++)
            {
                column.Add(0);
            }
            matrix.Add(column);
        }
        return matrix;
    }

    // currentBalance already reflects StartSpin()'s optimistic upfront deduction of the total bet,
    // so only the win is added back. Only reached if the server omits a balance.
    private static double CalculateNewBalance(double currentBalance, double winAmount)
    {
        return currentBalance + winAmount;
    }
}

#endregion
