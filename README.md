<p align="center">
  <img src="assets/icon.png" width="112" alt="Replay OBS">
</p>

<h1 align="center">Replay OBS</h1>

<p align="center">
  Мгновенный повтор поверх игр для <a href="https://obsproject.com">OBS Studio</a> — как <b>Alt+Z</b> у NVIDIA ShadowPlay.
</p>

<p align="center">
  <a href="https://github.com/erteterya/Replay-OBS/releases/latest"><img alt="Версия" src="https://img.shields.io/github/v/release/erteterya/Replay-OBS?label=%D0%B2%D0%B5%D1%80%D1%81%D0%B8%D1%8F&color=00D46A&labelColor=1A1D23"></a>
  <a href="https://github.com/erteterya/Replay-OBS/releases"><img alt="Загрузки" src="https://img.shields.io/github/downloads/erteterya/Replay-OBS/total?label=%D0%B7%D0%B0%D0%B3%D1%80%D1%83%D0%B7%D0%BA%D0%B8&color=00D46A&labelColor=1A1D23"></a>
  <a href="#установка"><img alt="Windows 10 | 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-1A1D23?logo=windows&logoColor=white&labelColor=1A1D23"></a>
  <a href="https://obsproject.com/download"><img alt="OBS Studio 30+" src="https://img.shields.io/badge/OBS%20Studio-30%2B-1A1D23?logo=obsstudio&logoColor=white&labelColor=1A1D23"></a>
  <a href="LICENSE"><img alt="Лицензия" src="https://img.shields.io/github/license/erteterya/Replay-OBS?label=%D0%BB%D0%B8%D1%86%D0%B5%D0%BD%D0%B7%D0%B8%D1%8F&color=1A1D23&labelColor=1A1D23"></a>
</p>

<p align="center">
  <a href="https://github.com/erteterya/Replay-OBS/releases/latest/download/ReplayOBS-Setup.exe"><img alt="Скачать для Windows" src="https://img.shields.io/badge/%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C%20%D0%B4%D0%BB%D1%8F%20Windows-00D46A?style=for-the-badge&logo=windows&logoColor=06140C"></a>
  <br><sub>Для ПК на Windows 10 / 11 · бесплатно, открытый код</sub>
</p>

<p align="center">
  <img src="docs/images/hero.png" alt="Меню Replay OBS поверх игры: вкладка «Мгновенный повтор», уведомление о сохранении и подсказка горячих клавиш">
</p>

## Что умеет

<table>
  <tr>
    <td width="50%">⏪ <b>Мгновенный повтор</b><br><sub>сохранить последние минуты игры одной клавишей (до 30 мин)</sub></td>
    <td width="50%">⏺️ <b>Запись</b><br><sub>«только игра» или «игра и экран», NVENC / AMF / Quick Sync / x264</sub></td>
  </tr>
  <tr>
    <td width="50%">📸 <b>Скриншоты</b><br><sub>игра, сцена, весь экран или активное окно</sub></td>
    <td width="50%">🎞️ <b>Галерея</b><br><sub>просмотр, обрезка, дорожки звука отдельно для CapCut</sub></td>
  </tr>
  <tr>
    <td width="50%">🙈 <b>Приватность</b><br><sub>окна Telegram, Discord, браузера скрываются из записи</sub></td>
    <td width="50%">🎮 <b>Игры находятся сами</b><br><sub>Steam, Epic, Riot, GOG, Ubisoft, EA, Xbox</sub></td>
  </tr>
  <tr>
    <td width="50%">👻 <b>Невидимо для зрителей</b><br><sub>меню нет ни в записи, ни в демонстрации Discord</sub></td>
    <td width="50%">🪶 <b>Лёгкое</b><br><sub>~0,5 МБ, без Electron и внедрения в игры</sub></td>
  </tr>
</table>

<table>
  <tr>
    <td align="center"><img src="docs/images/tab-record.png" width="260" alt="Вкладка «Запись»"><br><sub>Запись</sub></td>
    <td align="center"><img src="docs/images/tab-replay.png" width="260" alt="Вкладка «Мгновенный повтор»"><br><sub>Мгновенный повтор</sub></td>
    <td align="center"><img src="docs/images/tab-clips.png" width="260" alt="Вкладка «Галерея»"><br><sub>Галерея</sub></td>
  </tr>
</table>

## Установка

1. Установите [OBS Studio](https://obsproject.com/download) 30+.
2. Запустите [**ReplayOBS-Setup.exe**](https://github.com/erteterya/Replay-OBS/releases/latest/download/ReplayOBS-Setup.exe) — права администратора не нужны.
3. Нажмите **Alt+Z** в игре.

| Действие | Клавиши |
|---|---|
| Меню | `Alt+Z` |
| Сохранить повтор | `Alt+F10` |
| Запись | `Alt+F9` |
| Скриншот | `Alt+F8` |

## Подробнее

📖 [Руководство](docs/GUIDE.md) — сцены, звук, приватность, галерея, сборка из исходников<br>
📦 [Все версии](https://github.com/erteterya/Replay-OBS/releases)

<sub>[MIT](LICENSE) · OBS Studio — отдельный проект (GPL-2.0), Replay OBS общается с ним только через obs-websocket.</sub>
