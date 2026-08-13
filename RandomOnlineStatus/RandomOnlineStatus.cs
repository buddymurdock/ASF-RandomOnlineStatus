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
	private const ushort DefaultMaxDelayInMinutes = 240;
	private const ushort DefaultMinDelayInMinutes = 30;
	private const ushort DefaultOwnedGamesCacheHours = 24;

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

	public string Name => nameof(RandomOnlineStatus);
	public string RepositoryName => "buddymurdock/ASF-RandomOnlineStatus";
	public Version Version => typeof(RandomOnlineStatus).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomOnlineStatusEnabled / RandomOnlineStatusMinDelayMinutes / RandomOnlineStatusMaxDelayMinutes / RandomOnlineStatusMainGameAppIDs / RandomOnlineStatusOwnedGamesCacheHours from the global ASF.json config
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

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomOnlineStatus)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		if (MainGameAppIDs.Length == 0) {
			ASF.ArchiLogger.LogGenericWarning($"{nameof(RandomOnlineStatus)}MainGameAppIDs is empty; the 'main game' state will always fall back to no game.");
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, every {MinDelayInMinutes}-{MaxDelayInMinutes} minutes each bot randomly rolls: {OfflineWeight:P0} invisible, {(1 - OfflineWeight) * MainGameWeight:P0} online in a main game, {(1 - OfflineWeight) * IdleWeight:P0} online with no game, {(1 - OfflineWeight) * (1 - MainGameWeight - IdleWeight):P0} online in a random owned game.");

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

		// The connection is already gone, so there's nothing left to Resume() towards; just clear the flag so we don't try later
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

	private async Task BotStatusLoopAsync(Bot bot, CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			int delayMinutes = MinDelayInMinutes == MaxDelayInMinutes ? MinDelayInMinutes : Random.Shared.Next(MinDelayInMinutes, MaxDelayInMinutes + 1);

			try {
				await Task.Delay(TimeSpan.FromMinutes(delayMinutes), cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (cancellationToken.IsCancellationRequested || !bot.IsConnectedAndLoggedOn) {
				break;
			}

			try {
				await ApplyRandomStateAsync(bot).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	private static bool RollOffline() => Random.Shared.NextDouble() < OfflineWeight;

	// Only meaningful when RollOffline() returned false; splits the remaining (online) time between main game / idle / random game
	private static OnlineSubState RollOnlineSubState() {
		double roll = Random.Shared.NextDouble();

		if (roll < MainGameWeight) {
			return OnlineSubState.MainGame;
		}

		return roll < MainGameWeight + IdleWeight ? OnlineSubState.Idle : OnlineSubState.RandomGame;
	}

	private async Task ApplyRandomStateAsync(Bot bot) {
		if (RollOffline()) {
			bot.SteamFriends.SetPersonaState(EPersonaState.Invisible);
			StopSimulatedPlay(bot);

			bot.ArchiLogger.LogGenericInfo("Randomly went invisible (simulating offline).");

			return;
		}

		switch (RollOnlineSubState()) {
			case OnlineSubState.Idle:
				bot.SteamFriends.SetPersonaState(EPersonaState.Online);
				StopSimulatedPlay(bot);

				bot.ArchiLogger.LogGenericInfo("Randomly went online with no game.");

				break;
			case OnlineSubState.MainGame:
				bot.SteamFriends.SetPersonaState(EPersonaState.Online);

				await TryPlaySimulatedGameAsync(bot, true).ConfigureAwait(false);

				break;
			case OnlineSubState.RandomGame:
				bot.SteamFriends.SetPersonaState(EPersonaState.Online);

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
