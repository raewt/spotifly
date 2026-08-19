# Spotifly

Стеклянная тема для десктоп-клиента Spotify на Windows: обои, тонировка панелей, пресеты и кастомизатор.

Это не Spotify и не связано со Spotify AB.

![Главная](docs/home.png)

## Что умеет

- Обои: фото, GIF, беззвучные MP4/WebM
- Тонировка и блюр панелей
- Пресеты цветов и ручная палитра блоков
- Автоцвет текста
- Желе на плеере при скипе
- Кнопка с лапкой слева от Home

![Кастомизатор](docs/customizer.png)

## Файлы темы

| Путь | Зачем |
| --- | --- |
| `_xpui/unpacked/spotifly/theme.css` | Стекло, отступы, лапка |
| `_xpui/unpacked/spotifly/theme.js` | Кастомизатор, обои, анимации |
| `_xpui/unpacked/spotifly/paw.png` | Иконка кнопки |
| `_xpui/unpacked/spotifly/wallpaper.jpg` | Обои по умолчанию |

В `index.html` клиента:

```html
<link href="/spotifly/theme.css" rel="stylesheet">
<script defer src="/spotifly/theme.js"></script>
```

Папка `spotifly/` пакуется внутрь `xpui.spa` (zip, пути только с `/`).

Клиент и установщик сюда не входят: это чужие бинарники, плюс GitHub режет файлы больше 100 МБ.
