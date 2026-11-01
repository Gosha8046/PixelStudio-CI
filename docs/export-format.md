# Экспорт: PNG, спрайтшит и JSON

## Что экспортируется

| Экспорт | Файлы | Содержимое |
|---|---|---|
| Текущий кадр | `<имя>_frameN.png` | Композиция видимых слоёв кадра, RGBA, прозрачность сохраняется |
| Спрайтшит | `<имя>.png` + `<имя>.json` | Все кадры в порядке таймлайна в одной сетке и описание сетки |

Экспортируются **только данные документа**: в файлы попадают пиксели видимых слоёв. Пиксельная сетка, перекрестие, Onion Skinning и Tile Preview — элементы просмотра. Экспортёр (`Exporter`) их не видит: он работает только через `Compositor`, а оверлеи рисуются в отдельном `ViewRenderer` и в JS-холсте поверх сцены. Это закреплено тестами `Export_IsUnaffectedByViewOverlays` (unit) и `sprite sheet PNG and JSON agree…` (E2E).

## Раскладка спрайтшита (MVP)

- Все ячейки одного размера: размер холста. Прозрачные края не обрезаются, кадры не поворачиваются.
- Кадры идут слева направо, затем сверху вниз, в порядке таймлайна.
- `columns` задаёт число столбцов: от 1 до числа кадров. Большее значение уменьшается до числа кадров.
- `spacing` — прозрачный отступ **между** соседними ячейками (0–64 px). Внешнего поля нет.
- Размер атласа: `W = columns·w + (columns−1)·spacing`, `H = rows·h + (rows−1)·spacing`. Ограничения: не больше 8192 px по стороне и не больше 4096×4096 по площади.
- Пустые ячейки (если `frames < columns·rows`) и отступы полностью прозрачны.

PNG и JSON строятся из **одного** объекта `SpriteSheetLayout`, поэтому координаты и размеры в них всегда совпадают. Тест `SpriteSheet_PngAndJsonAgreeOnCoordinatesSizesAndDurations` декодирует PNG и проверяет, что каждый прямоугольник из JSON содержит ровно композицию своего кадра, а всё остальное прозрачно.

## JSON-схема

Формальная схема (JSON Schema 2020-12): [`spritesheet.schema.json`](spritesheet.schema.json).

| Поле | Тип | Смысл |
|---|---|---|
| `format` | `"pixelstudio-spritesheet"` | Идентификатор формата |
| `version` | `1` | Версия формата описания |
| `generator` | string | Программа и её версия |
| `image` | string | Имя PNG-файла **рядом** с JSON (без пути) |
| `atlas.width/height` | int | Размер атласа, px |
| `frameSize.width/height` | int | Размер ячейки, px |
| `layout.columns/rows/spacing` | int | Сетка и отступ |
| `frames[]` | array | По одному элементу на кадр в порядке таймлайна |
| `frames[].index` | int | Номер кадра (с 0) |
| `frames[].x/y` | int | Левый верхний угол в атласе, px; ось Y направлена **вниз** |
| `frames[].width/height` | int | Размер кадра, px |
| `frames[].durationMs` | int | Длительность кадра, мс: собственная или `round(1000 / FPS)` |
| `playback.order` | int[] | Порядок воспроизведения (индексы кадров) |
| `playback.loop` | bool | Зациклена ли анимация |
| `playback.totalDurationMs` | int | Сумма длительностей |

### Пример

Сгенерирован этой программой (`tools/PixelStudio.Bench -- --sample`): [`examples/slime.json`](examples/slime.json) и [`examples/slime.png`](examples/slime.png). Исходный проект: [`examples/slime.pxstudio`](examples/slime.pxstudio).

```json
{
  "format": "pixelstudio-spritesheet",
  "version": 1,
  "generator": "PixelStudio CI 0.1.0",
  "image": "slime.png",
  "atlas": { "width": 34, "height": 34 },
  "frameSize": { "width": 16, "height": 16 },
  "layout": { "columns": 2, "rows": 2, "spacing": 2 },
  "frames": [
    { "index": 0, "x": 0,  "y": 0,  "width": 16, "height": 16, "durationMs": 125 },
    { "index": 1, "x": 18, "y": 0,  "width": 16, "height": 16, "durationMs": 125 },
    { "index": 2, "x": 0,  "y": 18, "width": 16, "height": 16, "durationMs": 125 },
    { "index": 3, "x": 18, "y": 18, "width": 16, "height": 16, "durationMs": 250 }
  ],
  "playback": { "order": [0, 1, 2, 3], "loop": true, "totalDurationMs": 625 }
}
```

(В файле JSON отформатирован с отступами; здесь он сжат для краткости.)

## Совместимость с игровыми движками

Этот JSON — **универсальный, движково-нейтральный** формат. Он **не** совпадает с родными форматами Unity, Godot или Unreal Engine, и ни один из них не импортирует его «из коробки». PNG-атлас подходит любому движку. Нарезка на кадры и анимация требуют либо ручной настройки, либо импортирующего скрипта (адаптера). Адаптеры запланированы на следующий этап (см. [roadmap.md](roadmap.md)).

| Движок | Без скрипта | Где нужен импортирующий скрипт |
|---|---|---|
| Unity | PNG импортируется как Texture2D. В Sprite Editor можно вручную нарезать сетку: размер ячейки = `frameSize`, offset 0, padding = `spacing`. Длительности кадров при этом не переносятся | Editor-скрипт (`AssetPostprocessor` / `ScriptedImporter`): нарезка спрайтов по `frames[]` и создание `AnimationClip` с ключами по `durationMs` |
| Godot 4 | `AnimatedSprite2D` + `SpriteFrames`: в редакторе есть «Add frames from sprite sheet» с параметрами сетки (h/v frames, separation = `spacing`). Скорость там задаётся одной на анимацию, поэтому разные длительности кадров теряются | `@tool`-скрипт или `EditorImportPlugin`: `AtlasTexture` по `x/y/width/height`, длительность кадра как relative duration в `SpriteFrames` |
| Unreal Engine (Paper2D) | Текстура импортируется, спрайты можно вырезать вручную в редакторе Paper2D | Конвертер в формат, который понимает импортёр sprite sheet в Paper2D, либо Editor Utility / Python-скрипт: создание `PaperSprite` по прямоугольникам и `PaperFlipbook` с длительностями |

Координаты в JSON считаются от левого верхнего угла (Y вниз). В Unity Y направлен вверх, поэтому адаптер для Unity должен пересчитать координату: `y_unity = atlas.height − y − height`.
