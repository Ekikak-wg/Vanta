<p align="center">
  <img src="assets/icon.png" width="112" alt="Vanta">
</p>

<h1 align="center">Vanta</h1>

<p align="center">
  Мгновенный повтор поверх игр — как <b>Alt+Z</b> у NVIDIA ShadowPlay.<br>
  Записывает собственный скрытый движок на основе OBS Studio: ставится сам, окна не показывает.
</p>

<p align="center">
  <a href="https://github.com/Ekikak-wg/Vanta/releases/latest"><img alt="Версия" src="https://img.shields.io/github/v/release/Ekikak-wg/Vanta?label=%D0%B2%D0%B5%D1%80%D1%81%D0%B8%D1%8F&color=00D46A&labelColor=1A1D23"></a>
  <a href="https://github.com/Ekikak-wg/Vanta/releases"><img alt="Загрузки" src="https://img.shields.io/github/downloads/Ekikak-wg/Vanta/total?label=%D0%B7%D0%B0%D0%B3%D1%80%D1%83%D0%B7%D0%BA%D0%B8&color=00D46A&labelColor=1A1D23"></a>
  <a href="#установка"><img alt="Windows 10 | 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-1A1D23?logo=windows&logoColor=white&labelColor=1A1D23"></a>
  <a href="docs/GUIDE.md#движок-записи"><img alt="Движок OBS 32.2.2" src="https://img.shields.io/badge/%D0%B4%D0%B2%D0%B8%D0%B6%D0%BE%D0%BA-OBS%2032.2.2-1A1D23?logo=obsstudio&logoColor=white&labelColor=1A1D23"></a>
  <a href="LICENSE"><img alt="Лицензия GPL-2.0" src="https://img.shields.io/badge/%D0%BB%D0%B8%D1%86%D0%B5%D0%BD%D0%B7%D0%B8%D1%8F-GPL--2.0-1A1D23?labelColor=1A1D23"></a>
</p>

<p align="center">
  <a href="https://github.com/Ekikak-wg/Vanta/releases/latest/download/Vanta-Setup.exe"><img alt="Скачать для Windows" src="https://img.shields.io/badge/%D0%A1%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C%20%D0%B4%D0%BB%D1%8F%20Windows-00D46A?style=for-the-badge&logo=windows&logoColor=06140C"></a>
  <br><sub>Для ПК на Windows 10 / 11 · бесплатно, открытый код · OBS Studio ставить не нужно</sub>
</p>

<p align="center">
  <img src="docs/images/hero.png" alt="Меню Vanta поверх игры: вкладка «Мгновенный повтор», уведомление о сохранении и подсказка горячих клавиш">
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
    <td width="50%">🙈 <b>Приватность</b><br><sub>окна Telegram, Discord, браузера скрываются из записи; «Не беспокоить» на время записи</sub></td>
    <td width="50%">🎮 <b>Игры находятся сами</b><br><sub>Steam, Epic, Riot, GOG, Ubisoft, EA, Xbox</sub></td>
  </tr>
  <tr>
    <td width="50%">👻 <b>Невидимо для зрителей</b><br><sub>меню нет ни в записи, ни в демонстрации Discord</sub></td>
    <td width="50%">🎯 <b>Без пропусков кадров</b><br><sub>приоритет записи на видеокарте; движок OBS — любые видеокарты</sub></td>
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

1. Запустите [**Vanta-Setup.exe**](https://github.com/Ekikak-wg/Vanta/releases/latest/download/Vanta-Setup.exe) — права администратора не нужны.
2. Установщик сам скачает движок записи (официальный OBS Studio 32.2.2, 188 МБ) и сверит контрольную сумму. Ваш OBS Studio, если он есть, не трогается.
3. Нажмите **Alt+Z** в игре.

Обновление с Replay OBS (прежнее название) — просто запустите установщик Vanta: он сам перенесёт настройки и движок.

| Действие | Клавиши |
|---|---|
| Меню | `Alt+Z` |
| Сохранить повтор | `Alt+F10` |
| Запись | `Alt+F9` |
| Скриншот | `Alt+F8` |

## Подробнее

📖 [Руководство](docs/GUIDE.md) — движок, звук, приватность, галерея, ограничения, сборка из исходников<br>
📦 [Все версии](https://github.com/Ekikak-wg/Vanta/releases)

<sub>[GPL-2.0](LICENSE) · движок — [OBS Studio](https://obsproject.com) (GPL-2.0): установщик скачивает его официальный архив, кода OBS в репозитории нет.</sub>
