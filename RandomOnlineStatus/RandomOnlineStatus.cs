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
#pragma warning disable CA5394 // Randomness here only picks an arbitrary status/delay, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomOnlineStatus : IASF, IBotConnection, IGitHubPluginUpdates {
	private const ushort DefaultMinDelayInMinutes = 15;
	private const ushort DefaultMaxDelayInMinutes = 120;

	private static readonly EPersonaState[] DefaultStatuses = [EPersonaState.Online, EPersonaState.Away, EPersonaState.Busy, EPersonaState.Snooze, EPersonaState.Invisible];

	// One cancellable loop per currently connected bot, so we can stop rotating status the moment a bot goes offline
	private readonly ConcurrentDictionary<string, CancellationTokenSource> BotLoops = new(StringComparer.Ordinal);

	private bool Enabled;
	private ushort MaxDelayInMinutes = DefaultMaxDelayInMinutes;
	private ushort MinDelayInMinutes = DefaultMinDelayInMinutes;
	private EPersonaState[] Statuses = DefaultStatuses;

	public string Name => nameof(RandomOnlineStatus);
	public string RepositoryName => "buddymurdock/ASF-RandomOnlineStatus";
	public Version Version => typeof(RandomOnlineStatus).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomOnlineStatusEnabled / RandomOnlineStatusMinDelayMinutes / RandomOnlineStatusMaxDelayMinutes / RandomOnlineStatusStatuses from the global ASF.json config
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
					case $"{nameof(RandomOnlineStatus)}Statuses" when configValue.ValueKind == JsonValueKind.Array:
						HashSet<EPersonaState> parsedStatuses = [];

						foreach (JsonElement statusElement in configValue.EnumerateArray()) {
							if ((statusElement.ValueKind == JsonValueKind.String) && Enum.TryParse(statusElement.GetString(), true, out EPersonaState status) && Enum.IsDefined(status)) {
								parsedStatuses.Add(status);
							}
						}

						if (parsedStatuses.Count > 0) {
							Statuses = [.. parsedStatuses];
						}

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

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, will randomly rotate every bot's online status between [{string.Join(", ", Statuses)}], every {MinDelayInMinutes}-{MaxDelayInMinutes} minutes.");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	public Task OnBotDisconnected(Bot bot, EResult reason) {
		if (BotLoops.TryRemove(bot.BotName, out CancellationTokenSource? cts)) {
			cts.Cancel();
			cts.Dispose();
		}

		return Task.CompletedTask;
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
				EPersonaState currentStatus = bot.SteamFriends.GetPersonaState();

				EPersonaState[] candidates = Statuses.Length > 1 ? [.. Statuses.Where(status => status != currentStatus)] : Statuses;

				if (candidates.Length == 0) {
					continue;
				}

				EPersonaState newStatus = candidates[Random.Shared.Next(candidates.Length)];

				bot.SteamFriends.SetPersonaState(newStatus);
				bot.ArchiLogger.LogGenericInfo($"Randomly changed online status from {currentStatus} to {newStatus}.");
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}
}
#pragma warning restore CA5394 // Randomness here only picks an arbitrary status/delay, it's not used for anything security-sensitive
#pragma warning restore CA1812 // ASF uses this class during runtime
