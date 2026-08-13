# ASF-RandomOnlineStatus

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который для каждого залогиненного бота через случайный интервал времени меняет отображаемый онлайн-статус в Steam (Online/Away/Busy/Snooze/Invisible и т.д.) — чтобы профиль бота не висел неделями в одном и том же состоянии, как это обычно бывает у ботов, и больше походил на аккаунт живого человека.

У каждого бота свой независимый цикл: после логина бот ждёт случайное число минут в диапазоне `[MinDelayMinutes; MaxDelayMinutes]`, затем меняет статус на случайный из разрешённого списка (отличный от текущего), и снова ждёт. При дисконнекте бота его цикл останавливается и запускается заново при следующем логине. Смена статуса никак не влияет на фарм карточек — playtime считается независимо от отображаемого статуса.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomOnlineStatusEnabled": true,
	"RandomOnlineStatusMinDelayMinutes": 15,
	"RandomOnlineStatusMaxDelayMinutes": 120,
	"RandomOnlineStatusStatuses": ["Online", "Away", "Busy", "Snooze", "Invisible"]
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomOnlineStatusEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomOnlineStatusMinDelayMinutes` | `ushort`, минуты | `15` | Нижняя граница случайной паузы между сменами статуса. |
| `RandomOnlineStatusMaxDelayMinutes` | `ushort`, минуты | `120` | Верхняя граница случайной паузы между сменами статуса. |
| `RandomOnlineStatusStatuses` | `string[]` | `["Online", "Away", "Busy", "Snooze", "Invisible"]` | Пул статусов, между которыми плагин выбирает случайно. Допустимые значения — любые из `EPersonaState` Steam: `Offline`, `Online`, `Busy`, `Away`, `Snooze`, `LookingToTrade`, `LookingToPlay`, `Invisible`. |

Если `MinDelayMinutes` больше `MaxDelayMinutes`, значения меняются местами автоматически. Если после парсинга `RandomOnlineStatusStatuses` не осталось ни одного валидного значения, используется список по умолчанию.

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomOnlineStatus.git
cd ASF-RandomOnlineStatus
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt).
