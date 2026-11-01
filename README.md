# PC Doctor AI

Diagnóstico y reparación de computadoras desde un pendrive, pensado para que cualquier persona —incluso sin conocimientos— pueda entender qué le pasa a su PC y solucionarlo. Funciona **sin Internet**, **no instala nada** en la computadora, **no envía datos** y **nunca cambia nada sin preguntar**.

Si Windows no arranca o un virus no deja usar la PC, el pendrive tiene un **modo rescate** que arranca sin Windows: rescata fotos y documentos, busca virus y señales de hackeo con Windows apagado y revisa si el disco está fallando.

> Estado: versión 0.2 de prueba. Ver [docs/03-Estado-de-implementacion.md](docs/03-Estado-de-implementacion.md).

## Descargas

Las versiones están en [Releases](https://github.com/pendrivearg/pc-doctor/releases). Cada versión trae un manifiesto firmado con la clave del proyecto: los pendrives solo instalan versiones cuya firma es válida. Se compilan y prueban en GitHub a partir de este código ([Compilar y probar](https://github.com/pendrivearg/pc-doctor/actions)).

## Política de firma de código

La firma de código de Windows de PC Doctor la provee gratuitamente [SignPath.io](https://about.signpath.io), con certificado de [SignPath Foundation](https://signpath.org) *(solicitud en trámite: hasta que se apruebe, los programas se publican sin firma de Windows)*.

- Solo se firman programas compilados en GitHub Actions a partir del código de este repositorio.
- Cada firma la aprueba manualmente una persona del equipo.
- Roles del equipo:
  - **Autores** (pueden subir cambios): [pendrivearg](https://github.com/pendrivearg)
  - **Revisores** (revisan cambios de otras personas): [pendrivearg](https://github.com/pendrivearg)
  - **Aprobadores** (aprueban cada firma): [pendrivearg](https://github.com/pendrivearg)
- Privacidad: ver la [política de privacidad](PRIVACIDAD.md). En resumen, el programa no envía datos; solo se conecta a GitHub cuando la persona toca «Buscar actualizaciones».

## Principios

- **Local y gratuito:** todo corre en la propia PC, sin servicios pagos ni suscripciones.
- **Honesto:** separa lo comprobado de lo que no se pudo comprobar; nunca afirma haber solucionado algo sin verificarlo.
- **Seguro:** la IA no ejecuta comandos; solo se pueden usar reparaciones de un catálogo probado, con respaldo previo, verificación y deshacer.
- **Cero botones muertos:** lo que no funciona y no está probado no aparece en la interfaz.

## Documentación

- [Arquitectura y plan técnico](docs/01-Arquitectura-y-Plan-Tecnico.md)
- [Catálogo de problemas](docs/02-Catalogo-de-Problemas.md)
- [Estado de implementación](docs/03-Estado-de-implementacion.md)

## Estructura

```
src/PCDoctor.Core      motor de diagnóstico, reglas, biblioteca, reparaciones, historial
src/PCDoctor.Windows   sondas y acciones de Windows
src/PCDoctor.App       interfaz (Avalonia)
src/PCDoctor.Cli       herramienta de línea de comandos (técnicos y laboratorio)
knowledge/problems     biblioteca de problemas (YAML)
data/                  datos actualizables (fin de soporte de Windows, etc.)
tests/                 pruebas automáticas
tools/PCDoctor.Snap    capturas automáticas de la interfaz
packaging/             archivos que van al pendrive
```

## Compilar y probar

Requiere el SDK de .NET 10.

```bash
dotnet test tests/PCDoctor.Tests
dotnet run --project src/PCDoctor.Cli -- diagnosticar --tecnico
dotnet run --project src/PCDoctor.App
```

## Contribuir

La forma más útil de ayudar es sumar problemas a la biblioteca (`knowledge/problems/*.yaml`): cada ficha necesita frases reales de cómo lo cuenta la gente, una regla de detección sobre datos medidos (o pasos guiados), una salida clara y al menos una fuente oficial verificable. `pcdoctor-cli biblioteca` valida todo antes de aceptar cambios.

## Licencia

Copyright (C) 2026 los autores de PC Doctor AI.

Este programa es software libre: podés redistribuirlo y/o modificarlo bajo los términos de la **Licencia Pública General de GNU, versión 3** ([LICENSE](LICENSE)). Se distribuye con la esperanza de que sea útil, pero **sin ninguna garantía**.

En resumen: cualquiera puede usarlo, estudiarlo, modificarlo y venderlo, pero quien distribuya una versión modificada tiene que publicar su código bajo esta misma licencia. Los componentes de terceros y sus licencias están en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

El nombre y el logo «PC Doctor AI» identifican a este proyecto; la licencia del código no autoriza a presentar versiones modificadas como si fueran la oficial.
