using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomOnlineStatus;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA5394 // Randomness here only picks an arbitrary state/delay/game, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomOnlineStatus : IASF, IBotConnection, IGitHubPluginUpdates {
	private const ushort DefaultMaxDelayInMinutes = 480;
	private const ushort DefaultMinDelayInMinutes = 120;
	private const ushort DefaultOwnedGamesCacheHours = 24;
	private const ushort DefaultSubStateMaxDelayInMinutes = 90;
	private const ushort DefaultSubStateMinDelayInMinutes = 15;

	// Weights match a rough "human" daily pattern: mostly offline, and while online, mostly playing one particular game rather than idly browsing or trying something else
	private const double OfflineWeight = 0.70;
	private const double MainGameWeight = 0.70; // of the remaining (online) time
	private const double IdleWeight = 0.15; // of the remaining (online) time
	// RandomGameWeight is implicitly the rest: 1 - MainGameWeight - IdleWeight

	// One cancellable loop per currently connected bot, so we can stop the state machine the moment a bot goes offline
	private readonly ConcurrentDictionary<string, CancellationTokenSource> BotLoops = new(StringComparer.Ordinal);

	// Whether we currently have a plugin-initiated Actions.Play() session running for a bot (as opposed to real CardsFarmer activity), so we know when it's safe (and necessary) to Actions.Resume()
	private readonly ConcurrentDictionary<string, bool> BotSimulatingPlay = new(StringComparer.Ordinal);

	private readonly ConcurrentDictionary<string, (DateTime FetchedAt, Dictionary<uint, string> Games)> BotOwnedGamesCache = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, bool> NoOwnedMainGameWarned = new(StringComparer.Ordinal);

	private bool Enabled;
	private uint[] MainGameAppIDs = [];
	private ushort MaxDelayInMinutes = DefaultMaxDelayInMinutes;
	private ushort MinDelayInMinutes = DefaultMinDelayInMinutes;
	private ushort OwnedGamesCacheHours = DefaultOwnedGamesCacheHours;
	private ushort SubStateMaxDelayInMinutes = DefaultSubStateMaxDelayInMinutes;
	private ushort SubStateMinDelayInMinutes = DefaultSubStateMinDelayInMinutes;

	public string Name => nameof(RandomOnlineStatus);
	public string RepositoryName => "buddymurdock/ASF-RandomOnlineStatus";
	public Version Version => typeof(RandomOnlineStatus).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomOnlineStatusEnabled / RandomOnlineStatusMinDelayMinutes / RandomOnlineStatusMaxDelayMinutes / RandomOnlineStatusSubStateMinDelayMinutes /
	// RandomOnlineStatusSubStateMaxDelayMinutes / RandomOnlineStatusMainGameAppIDs / RandomOnlineStatusOwnedGamesCacheHours from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomOnlineStatus)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomOnlineStatus)}MinDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelay) && (minDelay > 0):
						MinDelayInMinutes = minDelay;

						break;
					case $"{nameof(RandomOnlineStatus)}MaxDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelay) && (maxDelay > 0):
						MaxDelayInMinutes = maxDelay;

						break;
					case $"{nameof(RandomOnlineStatus)}OwnedGamesCacheHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort cacheHours) && (cacheHours > 0):
						OwnedGamesCacheHours = cacheHours;

						break;
					case $"{nameof(RandomOnlineStatus)}SubStateMinDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort subStateMinDelay) && (subStateMinDelay > 0):
						SubStateMinDelayInMinutes = subStateMinDelay;

						break;
					case $"{nameof(RandomOnlineStatus)}SubStateMaxDelayMinutes" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort subStateMaxDelay) && (subStateMaxDelay > 0):
						SubStateMaxDelayInMinutes = subStateMaxDelay;

						break;
					case $"{nameof(RandomOnlineStatus)}MainGameAppIDs" when configValue.ValueKind == JsonValueKind.Array:
						HashSet<uint> parsedAppIDs = [];

						foreach (JsonElement appElement in configValue.EnumerateArray()) {
							if ((appElement.ValueKind == JsonValueKind.Number) && appElement.TryGetUInt32(out uint appID) && (appID != 0)) {
								parsedAppIDs.Add(appID);
							} else {
								ASF.ArchiLogger.LogGenericWarning($"Ignoring invalid {nameof(RandomOnlineStatus)}MainGameAppIDs entry: {appElement}.");
							}
						}

						MainGameAppIDs = [.. parsedAppIDs];

						break;
				}
			}
		}

		if (MinDelayInMinutes > MaxDelayInMinutes) {
			(MinDelayInMinutes, MaxDelayInMinutes) = (MaxDelayInMinutes, MinDelayInMinutes);
		}

		if (SubStateMinDelayInMinutes > SubStateMaxDelayInMinutes) {
			(SubStateMinDelayInMinutes, SubStateMaxDelayInMinutes) = (SubStateMaxDelayInMinutes, SubStateMinDelayInMinutes);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomOnlineStatus)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		if (MainGameAppIDs.Length == 0) {
			ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomOnlineStatus)}MainGameAppIDs is empty; the 'main game' state will always fall back to no game.");
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, every {MinDelayInMinutes}-{MaxDelayInMinutes} minutes each bot rolls a new offline/online block ({OfflineWeight:P0} offline), and while online re-rolls what it's doing every {SubStateMinDelayInMinutes}-{SubStateMaxDelayInMinutes} minutes: {MainGameWeight:P0} main game, {IdleWeight:P0} no game, {1 - MainGameWeight - IdleWeight:P0} random owned game.");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	public async Task OnBotDisconnected(Bot bot, EResult reason) {
		if (BotLoops.TryRemove(bot.BotName, out CancellationTokenSource? cts)) {
			await cts.CancelAsync().ConfigureAwait(false);
			cts.Dispose();
		}

		// Resume() is a no-op if CardsFarmer isn't paused, so it's safe to call unconditionally here. Without this, a bot that disconnects
		// while we're mid-simulated-play would reconnect with CardsFarmer left permanently paused (BotSimulatingPlay reset below means
		// nothing else would ever call Resume() for it again) and silently never farm cards until manually unpaused
		_ = bot.Actions.Resume();

		BotSimulatingPlay[bot.BotName] = false;
	}

	public Task OnBotLoggedOn(Bot bot) {
		if (!Enabled) {
			return Task.CompletedTask;
		}

		CancellationTokenSource cts = new();

		if (!BotLoops.TryAdd(bot.BotName, cts)) {
			// A loop for this bot is already running, nothing to do
			cts.Dispose();

			return Task.CompletedTask;
		}

		Utilities.InBackground(() => BotStatusLoopAsync(bot, cts.Token), true);

		return Task.CompletedTask;
	}

	// Two timing tiers, matching how a real person's day is structured: long offline/online blocks (hours), and within an online
	// block, shorter-lived changes to what's actually happening (minutes). Re-rolling offline-vs-online on the same short cadence
	// as "what game" would make the bot flicker its visibility every 30-240 minutes, which reads as far more robotic than a human
	// who's simply asleep or at work for a stretch. Block duration is drawn from the same range regardless of offline/online, so
	// the 70/30 time split falls out of the OfflineWeight roll alone and isn't skewed by this two-tier structure.
	private async Task BotStatusLoopAsync(Bot bot, CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			int blockMinutes = MinDelayInMinutes == MaxDelayInMinutes ? MinDelayInMinutes : Random.Shared.Next(MinDelayInMinutes, MaxDelayInMinutes + 1);
			DateTime blockEnd = DateTime.UtcNow.AddMinutes(blockMinutes);

			if (RollOffline()) {
				bot.SteamFriends.SetPersonaState(EPersonaState.Invisible);
				StopSimulatedPlay(bot);

				bot.ArchiLogger.LogGenericInfo($"Randomly went invisible (simulating offline) for ~{blockMinutes} minutes.");

				if (!await DelayUntilAsync(blockEnd, cancellationToken).ConfigureAwait(false)) {
					break;
				}
			} else {
				bot.ArchiLogger.LogGenericInfo($"Randomly came online for ~{blockMinutes} minutes.");

				while ((DateTime.UtcNow < blockEnd) && !cancellationToken.IsCancellationRequested) {
					try {
						await ApplyOnlineSubStateAsync(bot).ConfigureAwait(false);
					} catch (Exception e) {
						ASF.ArchiLogger.LogGenericException(e);
					}

					int subStateMinutes = SubStateMinDelayInMinutes == SubStateMaxDelayInMinutes ? SubStateMinDelayInMinutes : Random.Shared.Next(SubStateMinDelayInMinutes, SubStateMaxDelayInMinutes + 1);
					DateTime nextSubStateAt = DateTime.UtcNow.AddMinutes(subStateMinutes);
					DateTime waitUntil = nextSubStateAt < blockEnd ? nextSubStateAt : blockEnd;

					if (!await DelayUntilAsync(waitUntil, cancellationToken).ConfigureAwait(false)) {
						break;
					}
				}

				// Always hand control back to CardsFarmer at the end of an online block; if the next block is online too, it'll pick a fresh sub-state right away
				StopSimulatedPlay(bot);
			}

			if (!bot.IsConnectedAndLoggedOn) {
				break;
			}
		}
	}

	private static async Task<bool> DelayUntilAsync(DateTime until, CancellationToken cancellationToken) {
		TimeSpan delay = until - DateTime.UtcNow;

		if (delay > TimeSpan.Zero) {
			try {
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				return false;
			}
		}

		return !cancellationToken.IsCancellationRequested;
	}

	private static bool RollOffline() => Random.Shared.NextDouble() < OfflineWeight;

	private static OnlineSubState RollOnlineSubState() {
		double roll = Random.Shared.NextDouble();

		if (roll < MainGameWeight) {
			return OnlineSubState.MainGame;
		}

		return roll < MainGameWeight + IdleWeight ? OnlineSubState.Idle : OnlineSubState.RandomGame;
	}

	private async Task ApplyOnlineSubStateAsync(Bot bot) {
		bot.SteamFriends.SetPersonaState(EPersonaState.Online);

		switch (RollOnlineSubState()) {
			case OnlineSubState.Idle:
				StopSimulatedPlay(bot);

				bot.ArchiLogger.LogGenericInfo("Randomly online with no game.");

				break;
			case OnlineSubState.MainGame:
				await TryPlaySimulatedGameAsync(bot, true).ConfigureAwait(false);

				break;
			case OnlineSubState.RandomGame:
				await TryPlaySimulatedGameAsync(bot, false).ConfigureAwait(false);

				break;
		}
	}

	// Hands control back to CardsFarmer if we're the ones currently holding a simulated Play() session; no-op otherwise (including when the bot is simply farming for real)
	private void StopSimulatedPlay(Bot bot) {
		if (!BotSimulatingPlay.TryGetValue(bot.BotName, out bool simulating) || !simulating) {
			return;
		}

		BotSimulatingPlay[bot.BotName] = false;

		(bool success, string message) = bot.Actions.Resume();

		if (!success) {
			bot.ArchiLogger.LogGenericDebug($"{nameof(StopSimulatedPlay)}: {message}");
		}
	}

	private async Task TryPlaySimulatedGameAsync(Bot bot, bool onlyMainGames) {
		if (bot.CardsFarmer.NowFarming) {
			// The bot is already legitimately playing something for real - that already looks exactly like a human playing a game, nothing to fake here
			return;
		}

		Dictionary<uint, string>? ownedGames = await GetOwnedGamesCachedAsync(bot).ConfigureAwait(false);

		if ((ownedGames == null) || (ownedGames.Count == 0)) {
			return;
		}

		uint appID;

		if (onlyMainGames) {
			List<uint> ownedMainGames = [.. MainGameAppIDs.Where(ownedGames.ContainsKey)];

			if (ownedMainGames.Count == 0) {
				if (NoOwnedMainGameWarned.TryAdd(bot.BotName, true)) {
					bot.ArchiLogger.LogGenericWarning($"None of the configured {nameof(RandomOnlineStatus)}MainGameAppIDs are owned by this bot; the 'main game' state will fall back to no game for it.");
				}

				return;
			}

			appID = ownedMainGames[Random.Shared.Next(ownedMainGames.Count)];
		} else {
			List<uint> allOwned = [.. ownedGames.Keys];

			appID = allOwned[Random.Shared.Next(allOwned.Count)];
		}

		(bool success, string message) = await bot.Actions.Play([appID]).ConfigureAwait(false);

		if (success) {
			BotSimulatingPlay[bot.BotName] = true;

			bot.ArchiLogger.LogGenericInfo($"Randomly started playing {ownedGames[appID]} ({appID}) [{(onlyMainGames ? "main game" : "random game")}].");
		} else {
			bot.ArchiLogger.LogGenericWarning($"Failed to start playing {appID}: {message}");
		}
	}

	private async Task<Dictionary<uint, string>?> GetOwnedGamesCachedAsync(Bot bot) {
		if (BotOwnedGamesCache.TryGetValue(bot.BotName, out (DateTime FetchedAt, Dictionary<uint, string> Games) cached) && ((DateTime.UtcNow - cached.FetchedAt) < TimeSpan.FromHours(OwnedGamesCacheHours))) {
			return cached.Games;
		}

		Dictionary<uint, string>? ownedGames;

		try {
			ownedGames = await bot.ArchiHandler.GetOwnedGames(bot.SteamID).ConfigureAwait(false);
		} catch (Exception e) {
			ASF.ArchiLogger.LogGenericException(e);

			// Fall back to whatever we had cached before (possibly none), rather than failing this tick outright
			return cached.Games;
		}

		if (ownedGames != null) {
			BotOwnedGamesCache[bot.BotName] = (DateTime.UtcNow, ownedGames);
		}

		return ownedGames;
	}

	private enum OnlineSubState : byte {
		MainGame,
		Idle,
		RandomGame
	}
}
#pragma warning restore CA5394 // Randomness here only picks an arbitrary state/delay/game, it's not used for anything security-sensitive
#pragma warning restore CA1812 // ASF uses this class during runtime
