# Componentes de terceros

PC Doctor AI se distribuye bajo GPL-3.0 e incluye los siguientes componentes, todos con licencias compatibles. Este archivo se revisa en cada versión (ver sección 7 de la arquitectura).

| Componente | Uso | Licencia |
|---|---|---|
| .NET 10 (runtime y bibliotecas, incluidas System.Management, System.Diagnostics.EventLog y System.Speech) | Base del programa | MIT |
| Avalonia 11 (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Skia) | Interfaz | MIT |
| SkiaSharp / HarfBuzzSharp (incluidos por Avalonia) | Dibujo de la interfaz | MIT |
| ANGLE (incluido por Avalonia) | Aceleración gráfica | BSD-3-Clause |
| YamlDotNet | Lectura de la biblioteca | MIT |
| Microsoft.Data.Sqlite | Historial de reparaciones | MIT |
| SQLitePCLRaw | Acceso a SQLite | Apache-2.0 |
| SQLite | Base de datos | Dominio público |
| xUnit (solo pruebas, no se distribuye) | Pruebas automáticas | Apache-2.0 |
| llama.cpp (llama-server, build b12032) | Motor de IA local (proceso aparte) | MIT |
| Qwen3.5 0.8B / 2B / 4B (Alibaba Qwen), en formato GGUF de unsloth | Modelos de IA local | Apache-2.0 |

Los textos completos de cada licencia están disponibles en los repositorios oficiales de cada proyecto y se incluirán en la carpeta `licencias` de cada versión publicada.

Los contenidos de la biblioteca citan documentación oficial (Microsoft, Steam y otros) solo como **fuente verificable mediante enlaces**; no se copian sus textos.
