# PC DOCTOR AI — Arquitectura y Plan Técnico

**Versión:** 0.1 (Fase 1) · **Fecha:** 1 de noviembre de 2026 · **Estado:** propuesta para aprobación, antes de programar

Documento complementario: [02-Catalogo-de-Problemas.md](02-Catalogo-de-Problemas.md) (taxonomía de más de 1.100 problemas, incluidos los nuevos módulos agregados).

---

## 0. Resumen ejecutivo

| Decisión | Elección | Por qué, en una línea |
|---|---|---|
| Lenguaje del núcleo | **C# / .NET 10 LTS** (self-contained, recortado) | Integración nativa con Windows (CIM, SetupAPI, Event Log, WUA, Defender) sin instalar nada en la PC. |
| Interfaz | **Avalonia UI** | La misma interfaz corre en Windows **y** en el Linux de rescate (y en macOS a futuro). Liviana, MIT. |
| Motor de IA | **llama.cpp (`llama-server`)**, no Ollama | Mismo motor que usa Ollama, pero ~10× más chico, portable desde USB, con control fino de RAM/CPU/GPU. |
| Modelos | Escalera **Qwen3.5 0.8B / 2B / 4B / 9B** (Apache 2.0) en GGUF Q4; **Gemma 4 E2B/E4B** (Apache 2.0) como contendiente | Elección final por benchmark propio en español y uso de herramientas (Fase 1/5). |
| Sin IA o PC débil | **Motor de reglas + clasificador semántico** (embeddings de 118 M parámetros) | El producto funciona completo aunque la PC no pueda cargar ningún LLM. |
| Datos | **SQLite** + FTS5 (texto) + sqlite-vec (vectores) | Un archivo, sin servidor, rápido en HDD y en USB. |
| Antivirus | **Microsoft Defender de la propia PC** (motor principal) + **ClamAV + YARA** (segunda opinión y rescate) | Defender ya está instalado, actualizado y es el mejor motor gratuito disponible; ClamAV es redistribuible. |
| Rescate | 3 niveles: **WinRE de la propia PC** → **Linux de rescate firmado (Debian + shim)** → WinPE opcional que arma el técnico | WinPE no se puede redistribuir libremente; WinRE ya está licenciado en cada PC. |
| Actualizaciones | Paquetes firmados (Ed25519, esquema tipo TUF), deltas zstd, ranuras A/B, reversión | Gratis, incrementales, verificables, funcionan desde cualquier PC con Internet. |
| Garantía de calidad | **Laboratorio de averías**: cada problema se rompe a propósito en una VM, se detecta, se repara, se verifica y se revierte | Mecanismo concreto para que **ninguna opción visible falle**. |

### 0.1 Qué significa “solución para todo” (y qué no)

Ningún software puede reparar un disco quemado, una fuente rota ni una contraseña bancaria olvidada. Prometer eso sería mentir. Lo que **sí** garantiza la arquitectura es que **todo problema tiene una salida clara**, en uno de tres niveles:

| Nivel | Qué hace PC Doctor | Ejemplo |
|---|---|---|
| **AUTO** | Diagnostica, repara, verifica y puede revertir. | DNS roto, cola de impresión trabada, Windows Update bloqueado, complemento que cuelga Word. |
| **GUIADA** | Diagnostica y guía paso a paso (con imágenes y voz), verificando cada paso que se pueda verificar. | Activar la virtualización en la BIOS, conectar el monitor a la placa de video y no a la mother. |
| **DERIVA** | Diagnostica con evidencia y explica a quién acudir, qué pedir y qué decir, con un informe para el técnico o el soporte oficial. | Disco con sectores reasignados en aumento, ventilador trabado, cuenta Microsoft bloqueada. |

Límites duros que el producto declara siempre: fallas físicas, contraseñas/cuentas de terceros (solo recuperación oficial), discos cifrados sin clave de recuperación, ransomware sin descifrador público, firmware/BIOS dañado, hardware que Windows no puede medir (por ejemplo la salud real de una fuente).

### 0.2 Regla “cero botones muertos”

Pediste que **no haya nada obsoleto, sin uso, o que falle**. Eso se convierte en reglas de ingeniería verificables, no en buenas intenciones:

1. **Niveles de madurez por problema y por acción:**
   - **T0** documentado y revisado por un humano, con fuentes.
   - **T1** detección probada en laboratorio (VM con la falla inyectada).
   - **T2** reparación + verificación + reversión probadas en laboratorio, en Windows 10 y 11, sin efectos colaterales.
   - **T3** probado además en hardware real.
2. **Puerta de publicación:** una reparación automática solo aparece en la interfaz si está en **T2 o superior** para la versión de Windows y arquitectura de esa PC. Si no, se muestra como guía (GUIADA) o no se muestra. Esto lo hace cumplir la integración continua: si la interfaz referencia una acción sin pruebas aprobadas, **la compilación falla**.
3. **Banderas de función:** lo que no está terminado no se ve. Nunca un botón que diga “próximamente”.
4. **Auditoría de obsolescencia** en cada versión (sección 6.3): herramientas retiradas por Microsoft (WMIC, solucionadores `msdt`, VBScript, PowerShell 2.0, etc.) están prohibidas en el código.
5. **Cada reparación se verifica después**; si la verificación falla, se revierte automáticamente y se informa la verdad.
6. **Cero falsos positivos en PCs sanas:** en cada versión se corre el diagnóstico sobre imágenes limpias de Windows; si marca un “Problema detectado” inexistente, no se publica.

---

## 1. Arquitectura propuesta

### 1.1 Cuatro modos de funcionamiento

| Modo | Cuándo | Cómo arranca | Qué puede hacer |
|---|---|---|---|
| **A. Asistente en Windows** | Windows inicia (la gran mayoría de los casos). | El usuario abre el pendrive y hace doble clic en **“ABRIR PC DOCTOR”**. *Windows no permite autoejecución desde USB desde Windows 7; no se puede evitar ese doble clic.* | Todo: diagnóstico, chat, reparaciones, antivirus, juegos, redes, informes. |
| **B. Rescate desde el WinRE de la PC** | Windows no inicia pero su entorno de recuperación sí. | Opciones avanzadas → Símbolo del sistema → `D:\RESCATE` (herramienta nativa de PC Doctor, compilada AOT, sin dependencias). | Reparar arranque (`bcdboot`), SFC/DISM sin conexión, revertir actualizaciones pendientes, CHKDSK, desbloquear BitLocker con la clave del usuario, copiar archivos, informe. **No requiere tocar Secure Boot ni la BIOS.** |
| **C. Arranque desde el pendrive** | Ni Windows ni WinRE funcionan, o hay malware que impide trabajar desde adentro. | Menú de arranque de la PC (F12/F11/Esc según marca; PC Doctor guía según el fabricante detectado). | Escaneo antivirus sin iniciar el Windows infectado, SMART, recuperación de archivos, copia de seguridad de emergencia, prueba de RAM (Memtest86+), informe. |
| **D. WinPE del técnico** (opcional) | Técnicos que quieren herramientas Windows completas al arrancar. | El instalador arma un WinPE **en la PC del técnico** usando el Windows ADK que él descarga de Microsoft. | Igual que B pero arrancando desde el USB. |

### 1.2 Procesos en el Modo A

```
                           ┌───────────────────────────────────────────┐
  Usuario ───────────────► │ PCDoctor.exe  (interfaz, SIN privilegios)  │
  (texto, voz, botón       │  • Orquestador de casos (máquina de estados)│
   "Analizar y             │  • Intérprete: clasificador → LLM opcional │
   solucionar")            │  • Motor de diagnóstico: sondas + reglas    │
                           │  • Biblioteca (SQLite + FTS5 + vectores)    │
                           │  • Informes · Historial · Actualizador      │
                           └──────┬──────────────────────────┬──────────┘
                                  │ canal local con ACL      │ HTTP solo 127.0.0.1
                                  │ + desafío por sesión     │ + token aleatorio
                                  ▼                          ▼
              ┌──────────────────────────────┐   ┌─────────────────────────────┐
              │ PCDoctor.Agent.exe (ELEVADO) │   │ llama-server.exe            │
              │ se abre con UAC solo cuando  │   │ dentro de un Job Object:    │
              │ hace falta                   │   │ tope de RAM, hilos, prioridad│
              │  • Ejecutor del CATÁLOGO     │   │ baja; se descarga al terminar│
              │  • Respaldos y diario        │   └─────────────────────────────┘
              │  • Verificación y reversión  │
              │  • Sensores (con permiso)    │
              └──────────────────────────────┘
```

Claves del diseño:

- **La interfaz nunca corre como administrador.** Solo el agente se eleva, solo cuando una acción lo necesita, y se cierra al terminar. **Un solo aviso de Windows por sesión:** el agente se abre la primera vez que la persona toca «Solucionar» en algo que lo requiere y atiende todas las reparaciones siguientes sin volver a preguntar. Antes del aviso, PC Doctor muestra en letra grande qué va a aparecer y que hay que tocar «Sí». Si el usuario no es administrador, Windows pide la clave del administrador en su propia ventana de UAC; **PC Doctor nunca ve ni guarda esa clave**.
- **El LLM corre en un proceso aparte** con límite duro de memoria (Job Object). Si se queda sin memoria o se cuelga, se mata ese proceso y la aplicación sigue funcionando con el motor de reglas.
- **El LLM no tiene acceso a ninguna herramienta.** Solo devuelve un JSON restringido por gramática (“preguntar X”, “correr sonda Y”, “proponer reparación Z del catálogo”). El orquestador valida cada pedido contra el catálogo y la política.

### 1.3 Componentes

| Componente | Responsabilidad | Notas de implementación |
|---|---|---|
| **Interfaz (UI)** | Modo sencillo y modo técnico, chat, botón principal, informes, historial. | Avalonia; renderizado por software si la GPU es antigua; sin animaciones costosas; textos grandes; navegación por teclado; lector de pantalla (UI Automation). |
| **Orquestador de casos** | Lleva cada caso por los 11 pasos (sección 1.4). Persiste el estado para sobrevivir a reinicios. | Máquina de estados explícita; cada transición queda en el diario. |
| **Intérprete** | Pasar de “no me anda el Internet” a síntomas estructurados. | Nivel 1: clasificador por embeddings contra miles de frases coloquiales de la biblioteca (funciona en PCs de 4 GB). Nivel 2: LLM para conversación, repreguntas y explicación. |
| **Motor de diagnóstico** | Recolectar evidencia y decidir. | **Sondas** (solo lectura, VERDES) producen **hechos** tipados. **Reglas** declarativas de la biblioteca evalúan hechos y producen **hallazgos** con estado y evidencia. |
| **Biblioteca** | Problemas, causas, reglas, reparaciones, frases, fuentes. | Fuente en YAML (repositorio), compilada a SQLite firmado. |
| **Motor de reparación** | Ejecutar solo acciones del catálogo, con política, respaldo, verificación y reversión. | Vive en el agente elevado. Ver sección 1.6. |
| **Sensores** | Temperaturas, ventiladores, voltajes, uso de GPU. | LibreHardwareMonitorLib (requiere controlador PawnIO firmado; se pide permiso), NVML (NVIDIA), ADLX (AMD), CIM de almacenamiento. |
| **Seguridad** | Antivirus, persistencias sospechosas, cuarentena, estafas. | Defender (API WMI/MpCmdRun), ClamAV, YARA, módulo propio de persistencias. |
| **Informes** | Informe sencillo e informe técnico. | HTML autocontenido (abre en cualquier navegador; se imprime a PDF con “Microsoft Print to PDF”). |
| **Actualizador** | Paquetes firmados, deltas, A/B, reversión. | Sección 9. |
| **CLI sin interfaz** | `pcdoctor-cli diagnose/fix/verify --json` | Usado por el laboratorio de averías y por técnicos. |

### 1.4 Flujo de un caso — ejemplo: «Mi computadora está muy lenta y no puedo abrir Word»

| Paso | Qué ocurre | Componente |
|---|---|---|
| 1. Comprender | Síntomas: `RENDIMIENTO_LENTO` + `APP_NO_ABRE(app=Word)`. Si es ambiguo, repregunta (“¿Word no abre nada, o abre y se cierra?”). | Intérprete |
| 2. Examinar | Muestreo de 30 s de CPU/RAM/disco y procesos; salud y espacio de disco; programas de inicio; estado de Defender; tipo de Office (Click-to-Run/MSI), versión, licencia; eventos `Application Error 1000` de `WINWORD.EXE` con su módulo culpable; complementos. | Sondas VERDES |
| 3. Causas posibles | Las reglas de la biblioteca proponen candidatos con evidencia. | Reglas |
| 4. Consultar biblioteca | `PERF-DSK-004` (disco al 100 % en HDD con poco espacio), `APP-OFF-012` (complemento COM que hace fallar Word). | Biblioteca |
| 5. Pruebas | Abrir Word en modo seguro (`winword /safe`) con permiso: abre → se confirma el complemento. | Acción VERDE/AMARILLA |
| 6. Causa más probable | “**Confirmado:** Word se cierra por el complemento X (evento del 30/10 + abre en modo seguro). **Confirmado:** el disco está al 100 % y quedan 0,8 GB libres. **Hipótesis:** el disco rígido es lento por antigüedad (SMART sin errores; no se puede afirmar más).” | Motor + LLM explica |
| 7. Proponer | Desactivar el complemento (🟡 reversible). Liberar espacio seguro: temporales, caché de actualizaciones, papelera **solo si el usuario acepta** (🟡). Sugerencia de mejora: SSD (DERIVA). | Biblioteca |
| 8. Autorización | Un botón por acción, explicando qué cambia y cómo se deshace. | UI + política |
| 9. Ejecutar | Respaldo de la clave de registro del complemento → desactivar. Limpieza con lista exacta de lo borrado. | Agente |
| 10. Verificar | Abrir Word normalmente y comprobar por UI Automation que aparece su ventana principal sin error; medir espacio libre. Si falla: reversión automática. | Agente + sondas |
| 11. Informar | “Listo: Word abre. Liberamos 6,2 GB. El disco sigue siendo lento por ser mecánico: la mejora recomendada es un SSD.” | Informes |

### 1.5 Cómo se evita que la IA invente

1. **Separación hechos / hipótesis.** Los hallazgos los produce el motor de reglas con evidencia (eventos, valores medidos). El LLM **no puede crear hallazgos**; solo puede explicarlos.
2. **Estados obligatorios** en todo resultado: `Correcto` · `Advertencia` · `Problema detectado` · `Requiere revisión física` · `No se pudo comprobar`.
3. **El veredicto lo redacta el motor, no el LLM.** La frase “Solucionado” solo puede aparecer si la verificación de esa reparación dio `ok`. Un filtro de salida bloquea textos del LLM que afirmen éxito, borren hipótesis o mencionen acciones que no se ejecutaron.
4. **Salida estructurada con gramática** (JSON Schema/GBNF en llama.cpp): el modelo no puede producir comandos de consola.
5. **Citas internas:** cada explicación referencia los IDs de hallazgos y de problemas de la biblioteca; en modo técnico se ven.
6. **Evaluación continua:** un conjunto de 2.000+ conversaciones de prueba mide alucinaciones y aciertos antes de cada versión del modelo o de los prompts.

### 1.6 Motor de reparación y modelo de seguridad

**Catálogo de acciones** — cada acción es un manifiesto firmado; no existe forma de ejecutar texto libre:

```yaml
id: net.dns.set_dhcp
titulo: "Volver a obtener los DNS automáticamente"
riesgo: amarillo
requiere_admin: true
reinicio: no
parametros:
  adaptador: { tipo: guid, origen: "hecho:red.adaptadores[*].guid" }
precondiciones: [ "hecho:red.adaptadores[adaptador].existe" ]
respaldo: [ snapshot:red.config_dns(adaptador) ]
ejecutar: builtin:NetDns.SetDhcp          # código C# firmado, no texto del LLM
verificar: { sonda: red.resolver_dns, esperar: ok, plazo: 20s }
revertir: restaurar_snapshot
tiempo_maximo: 30s
madurez: { win10_22h2_x64: T2, win11_25h2_x64: T2, win11_25h2_arm64: T1 }
```

| Color | Qué incluye | Consentimiento |
|---|---|---|
| 🟢 **VERDE** | Lecturas, sondas, pruebas no destructivas, abrir una configuración de Windows. | Ninguno; queda en el registro de actividad visible. |
| 🟡 **AMARILLO** | Reparaciones reversibles: servicios, red, controladores (con copia del anterior), registro (con exportación previa), desinstalaciones, SFC/DISM, limpieza de temporales. | Botón “Sí, hacelo” por acción (o por plan, mostrando la lista). Respaldo previo obligatorio. |
| 🔴 **ROJO** | Formatear, particionar, convertir MBR→GPT, restablecer Windows, reinstalar, firmware/BIOS, borrar perfiles. | Doble confirmación con explicación de consecuencias; exige copia de seguridad verificada de los archivos personales; en Modo sencillo se ofrece con ayuda de otra persona o se deriva. |

**Prohibiciones codificadas** (no se pueden activar desde la biblioteca ni desde el LLM):
borrar archivos personales automáticamente · desactivar antivirus, firewall o protecciones · descargar ejecutables de dominios fuera de la lista oficial fijada y sin firma Authenticode válida · modificar licencias, activar software sin licencia o evadir protecciones · extraer, mostrar o guardar contraseñas · instalar acceso remoto · correr `chkdsk /r` o escaneos de superficie sobre un disco con SMART en falla antes de rescatar los datos.

**Respaldos y reversión**, según el tipo de cambio:
- Registro: exportación de cada clave tocada.
- Servicios: tipo de inicio y estado previos.
- Controladores: `pnputil /export-driver` del paquete anterior antes de cambiarlo.
- Archivos de sistema renombrados, nunca borrados (por ejemplo `SoftwareDistribution` → `SoftwareDistribution.pcdoctor-AAAAMMDD`).
- Configuración de red/proxy/DNS: snapshot exacto.
- Punto de restauración del sistema cuando esté habilitado. *Limitación real:* Windows crea como máximo uno cada 24 h por defecto y muchas PCs lo tienen desactivado; por eso nunca es el único respaldo.

**Diario:** SQLite en el pendrive, por PC, solo agregar, encadenado por hashes (detecta manipulación). La pantalla **Historial de reparaciones** lista cada cambio con su botón **Deshacer**.

**Reinicios:** si una reparación necesita reiniciar (por ejemplo `netsh winsock reset` o `chkdsk /f`), con permiso se deja un verificador mínimo en `%ProgramData%\PCDoctor\resume` y una entrada `RunOnce`; tras el reinicio comprueba el resultado, lo guarda y se borra solo.

### 1.7 Huella en la PC anfitriona

Por defecto **no se instala nada**. Se escribe solamente en `%TEMP%\PCDoctor` (se borra al salir). Excepciones, siempre con aviso: el controlador de sensores PawnIO (opcional; se ofrece desinstalarlo al cerrar), el verificador post-reinicio y los cambios propios de cada reparación. Lo que Windows registra por sí mismo (Prefetch, historial de dispositivos USB) no se puede evitar y se documenta.

### 1.8 Estructura modular del código

```
src/
  PCDoctor.App            UI Avalonia (Windows y Linux)
  PCDoctor.Core           orquestador, política, modelos de datos
  PCDoctor.Agent          proceso elevado: ejecutor, respaldos, diario
  PCDoctor.Probes.*       sondas por área (Hardware, Windows, Red, Office, Juegos, Seguridad…)
  PCDoctor.Actions.*      implementaciones de acciones por área
  PCDoctor.Rules          evaluador del lenguaje de reglas (sin ejecución de código)
  PCDoctor.Knowledge      acceso a la biblioteca (FTS5 + vectores)
  PCDoctor.AI             adaptador llama-server, perfiles, guardas de salida
  PCDoctor.Security       Defender, ClamAV, YARA, persistencias, cuarentena
  PCDoctor.Update         cliente de actualizaciones
  PCDoctor.Rescue         herramienta nativa AOT para WinRE/WinPE
  PCDoctor.Cli            interfaz de línea de comandos (laboratorio y técnicos)
knowledge/                biblioteca en YAML + esquema JSON + frases coloquiales
lab/                      scripts de inyección de fallas, VMs, casos de prueba
rescue-linux/             receta de la imagen de rescate (live-build)
packaging/                creador del pendrive, firmas, manifiestos
```

Cada área (`Probes.Red` + `Actions.Red` + su parte de la biblioteca) es un **módulo** con versión propia: se actualiza o reemplaza sin recompilar el resto.

---

## 2. Tecnologías elegidas y justificación

| Área | Elegido | Descartado / alternativa | Razón |
|---|---|---|---|
| Lenguaje núcleo | **C# .NET 10 LTS** (soporte hasta nov. 2028), publicación self-contained + trimming | Python como núcleo | Python obliga a cargar un intérprete (30–100 MB) y es más lento al iniciar; C# llama directo a CIM, SetupAPI, CfgMgr32, Event Log, WUA, COM de Firewall/Tareas, DXGI. Python queda para herramientas internas (compilar la biblioteca, laboratorio). |
| Interfaz | **Avalonia** | WPF (solo Windows), WinUI 3 (runtime extra, más pesado), Electron (150–300 MB de RAM), Tauri/WebView2 | Avalonia permite reutilizar la misma UI en el rescate Linux y en macOS futuro. RAM ~100–150 MB. Riesgo: accesibilidad en Linux menos madura → se compensa con voz integrada. |
| Herramienta de rescate en WinRE | **.NET Native AOT** (consola/TUI) | Scripts batch | WinRE no tiene .NET; un binario AOT corre sin dependencias. |
| Inferencia | **llama.cpp `llama-server`** (MIT) con builds CPU-AVX2, CPU sin AVX, **Vulkan** y ARM64 | **Ollama** | Ollama usa llama.cpp por dentro, pero agrega un servicio residente, su propio almacén de modelos y un paquete de Windows de más de 1 GB (librerías CUDA/ROCm). llama.cpp pesa decenas de MB, lee GGUF directo del USB, y deja fijar hilos, contexto, capas en GPU y caché KV. Vulkan cubre NVIDIA, AMD e Intel sin CUDA. Build CUDA como descarga opcional. |
| Base de datos | **SQLite** (dominio público) + **FTS5** + **sqlite-vec** (MIT/Apache) | PostgreSQL, bases vectoriales con servidor | Un archivo, cero servicios, ideal para un USB. |
| Búsqueda (RAG) | Híbrida: BM25 (FTS5) + similitud de embeddings + coincidencia de reglas sobre hechos reales | Solo vectores | El texto coloquial se resuelve mejor combinando las tres señales; y las reglas usan datos de la PC, no solo texto. |
| Embeddings | **multilingual-e5-small** (MIT, 118 M, ONNX int8 ≈ 35 MB) vía **ONNX Runtime** (MIT) | Modelos de embeddings de 0,6 B o más | Buen español, corre en PCs de 4 GB. Alternativas a medir: Qwen3-Embedding-0.6B. |
| Reglas | **Lenguaje declarativo propio** (`all/any/not`, comparaciones sobre rutas de hechos), compilado a AST | Scripts, Lua, motores de reglas genéricos | Seguro (no ejecuta código), fácil de validar y probar. |
| Sensores | **LibreHardwareMonitorLib** (MPL-2.0) + **PawnIO** (controlador firmado), **NVML**, **AMD ADLX**, CIM `MSFT_StorageReliabilityCounter` | WinRing0 | WinRing0 tiene una vulnerabilidad conocida (CVE-2020-14979) y Defender lo marca como herramienta de hacking desde 2025. PawnIO es el reemplazo adoptado por el ecosistema. **Conflicto conocido:** el anticheat FACEIT bloquea PawnIO → se instala solo con permiso y se ofrece quitarlo al terminar. |
| SMART | CIM de almacenamiento (primero) + **smartctl** (smartmontools, GPL-2, binario separado) | Herramientas propietarias | Cubre SATA, NVMe y la mayoría de puentes USB. |
| Antivirus | **Defender** (la PC ya lo tiene) + **ClamAV** (GPL-2) + **YARA** (BSD-3) | Incluir motores comerciales | Los escáneres gratuitos de Malwarebytes, ESET, Kaspersky, etc. **no se pueden redistribuir**; se ofrecen solo como enlace oficial. |
| Voz | Salida: voces de Windows (OneCore, sin conexión). Entrada: **whisper.cpp** (MIT) con modelo `base` multilingüe. En Linux: espeak-ng/Piper. | Dictado en la nube | Funciona sin Internet; útil para personas mayores. |
| Leer un mensaje de error | **UI Automation** (lee el texto de la ventana de error directamente) → **Windows.Media.Ocr** como respaldo | Visión con LLM | Exacto, instantáneo, sin modelo. La visión de Qwen3.5/Gemma 4 se evaluará como extra. |
| Rescate arrancable | **Debian 13 live** mínimo con **shim y kernel firmados** + Memtest86+ | Ventoy como base; WinPE redistribuido | Ventoy obliga a registrar claves MOK con Secure Boot; WinPE tiene restricciones de redistribución. Debian arranca con Secure Boot en la mayoría de las PCs. |
| Firmas de actualizaciones | **Ed25519** (NSec/libsodium) + esquema tipo **TUF**; deltas **zstd `--patch-from`** (BSD) | Solo HTTPS | HTTPS no protege si el servidor es comprometido; las firmas sí. |
| Firma de código | Authenticode **gratuita para código abierto** (SignPath Foundation) | Certificados pagos (descartados: costo cero) | Sin firma, UAC muestra «Editor desconocido», el Control inteligente de aplicaciones de Windows 11 puede bloquear el programa y los antivirus dan más falsos positivos. Como el proyecto es GPL-3.0, califica para firma gratuita. |

---

## 3. Distribución de los 32 GB

Un pendrive de “32 GB” tiene ~29–29,8 GB reales. Se planifica sobre **29 GB**.

| Partición / carpeta | Contenido | Tamaño |
|---|---|---|
| **P1 · FAT32 `PCD-BOOT`** | Arranque UEFI (shim + GRUB firmados), Linux de rescate (kernel, initrd, squashfs con la app Linux, ClamAV, smartmontools, TestDisk/PhotoRec, ddrescue, dislocker, voz), Memtest86+ | **4,0 GB** (≈2,2 en uso + margen de actualización) |
| **P2 · exFAT `PC DOCTOR`** | | **25,0 GB** |
| `ABRIR PC DOCTOR.exe` + `/app` | Builds Windows x64, ARM64, x86 + herramienta de rescate AOT | 0,5 GB |
| `/engines` | llama.cpp (AVX2, sin AVX, Vulkan, ARM64), whisper.cpp, ONNX Runtime | 0,3 GB |
| `/models` | Qwen3.5 0.8B (≈0,6) · 2B (≈1,4) · 4B (≈2,8) · 9B (≈5,8) en Q4_K_M · embeddings (0,04) · whisper base (0,15) | ≈ 10,8 GB |
| `/knowledge` | Biblioteca SQLite (texto, reglas, vectores) + imágenes de guías (BIOS, puertos, etc.) | 0,6 GB |
| `/data` | Requisitos de juegos, tabla de rendimiento relativo de CPU/GPU, listas de CPUs compatibles con Windows 11, PCI/USB IDs, códigos de error de dispositivos, bugchecks | 0,15 GB |
| `/security` | ClamAV para Windows + base de firmas, reglas YARA, definiciones sin conexión de Defender (x64/x86/ARM64, descargadas de Microsoft al actualizar) | 1,2 GB |
| `/runtimes` | Visual C++ 2005–2022 (x86/x64/ARM64), DirectX End-User Runtime, .NET Framework 4.8.1, .NET Desktop Runtimes vigentes, Java (Eclipse Temurin), XNA 4.0, OpenAL | 0,9 GB |
| `/tools` | smartctl, 7-Zip, SumatraPDF, TestDisk/PhotoRec | 0,4 GB |
| `/drivers` | Controladores de red básicos (Ethernet/Wi-Fi Intel, Realtek, MediaTek) para PCs que quedaron sin Internet tras reinstalar — **sujeto a la licencia de cada fabricante (verificar en Fase 1)** | 1,0 GB |
| `/updates` | Zona de preparación A/B | 2,5 GB |
| `/state` | Por PC: diario, informes, respaldos de registro/controladores, cuarentena cifrada | 2,0 GB |
| Libre | Margen, copias de emergencia de documentos | ≈ 4,6 GB |

**Ediciones según el pendrive del usuario** (el creador detecta capacidad y velocidad):

| Edición | Pendrive | Diferencias |
|---|---|---|
| **Lite** | 16 GB | Sin modelos 4B/9B ni paquete de controladores. IA hasta 2B. |
| **Estándar** | 32 GB | La tabla de arriba. |
| **Pro / Técnico** | 64 GB o más | Modelos adicionales, paquetes de controladores ampliados, espacio para guardar la ISO oficial de Windows **descargada por el usuario desde Microsoft** para reinstalaciones. |

Notas: exFAT para P2 porque Windows, Linux y macOS lo leen nativamente; para reducir el riesgo de corrupción por desconexión brusca, todo lo grande (modelos, biblioteca, herramientas) es **de solo lectura y verificado por hash**, y solo `/state` y `/updates` se escriben, con escrituras atómicas. El creador del pendrive **detecta pendrives falsificados** (los que dicen 32 GB y tienen 8) con una prueba de escritura/lectura antes de instalar.

---

## 4. Consumo estimado de RAM

### 4.1 Por componente (memoria de trabajo aproximada; se medirá en Fase 2/5)

| Componente | RAM | Cuándo está cargado |
|---|---|---|
| Interfaz + núcleo (.NET + Avalonia) | 120–180 MB | Siempre |
| Agente elevado + sensores | 40–80 MB | Durante reparaciones / lectura de sensores |
| Biblioteca SQLite (cachés) | 30–60 MB | Siempre |
| Clasificador de embeddings | 100–200 MB | Al interpretar texto; se libera luego |
| LLM Qwen3.5 0.8B Q4 | ≈ 0,8–1,0 GB | Bajo demanda |
| LLM 2B Q4 | ≈ 1,6–2,0 GB | Bajo demanda |
| LLM 4B Q4 | ≈ 3,0–3,5 GB | Bajo demanda |
| LLM 9B Q4 | ≈ 6,0–7,0 GB | Bajo demanda o en VRAM |
| Whisper base (voz) | ≈ 0,3 GB | Solo mientras transcribe |
| ClamAV con base completa | **≈ 1,0–1,5 GB** | Solo durante el escaneo |

**PC de 4 GB típica:** Windows 10/11 con un navegador ya usa 2,5–3 GB. Queda ~1–1,5 GB. PC Doctor sin IA generativa ocupa **≈ 250–400 MB**: entra cómodo. La IA generativa normalmente **no** entra sin cerrar otros programas; PC Doctor lo ofrece (“Si cerrás el navegador puedo activar el asistente conversacional”) pero **todas las funciones de diagnóstico y reparación siguen disponibles** con reglas y clasificador. ClamAV completo tampoco entra: en esas PCs el escaneo usa Defender, que ya está residente.

### 4.2 Selección de perfil por memoria **realmente libre**

`presupuesto = RAM disponible − margen` (margen = el mayor entre 1 GB y 15 % de la RAM total). Si hay GPU con VRAM libre suficiente (medida con DXGI), se suma.

| Presupuesto | Perfil | IA |
|---|---|---|
| < 0,9 GB | **Básico** | Reglas + clasificador. Chat con respuestas de la biblioteca. |
| 0,9 – 2,0 GB | **Básico IA** | 0.8B, contexto 4K |
| 2,0 – 3,6 GB | **Equilibrado liviano** | 2B, contexto 4–8K |
| 3,6 – 7,0 GB | **Equilibrado** | 4B, contexto 8K |
| ≥ 7,0 GB o VRAM ≥ 6 GB | **Avanzado** | 9B (con capas en GPU si hay) |

Ajustes automáticos adicionales:
- **CPU sin AVX/AVX2** (muchos Celeron/Pentium Silver baratos): build sin AVX y tope en 2B, porque más grande sería demasiado lento.
- **Puerto USB 2.0** (~30 MB/s): cargar un modelo de 2,8 GB tardaría ~90 s. Se ofrece copiarlo al disco de la PC temporalmente (si hay espacio y el usuario acepta) y borrarlo al salir.
- **Límites duros:** Job Object con `ProcessMemoryLimit`, hilos = núcleos físicos − 1, prioridad “por debajo de lo normal”, EcoQoS en Windows 11. Un vigilante descarga el modelo si la memoria disponible baja de 500 MB o tras 2 minutos sin uso.
- **El usuario puede fijar el perfil** en el modo técnico.

---

## 5. Modelo de IA local recomendado

**Principio:** el producto es **independiente del modelo**. Cualquier modelo GGUF que pase la evaluación se puede cambiar por actualización sin tocar el programa.

| Rol | Candidato principal | Contendiente | Licencia |
|---|---|---|---|
| Básico IA | Qwen3.5-0.8B | Gemma 4 E2B | Apache 2.0 (ambos, según la información disponible; confirmar en la ficha oficial) |
| Equilibrado liviano | Qwen3.5-2B | Gemma 4 E2B | Apache 2.0 |
| Equilibrado | Qwen3.5-4B | Gemma 4 E4B | Apache 2.0 |
| Avanzado | Qwen3.5-9B | Gemma 4 E4B / modelos MoE en edición Pro | Apache 2.0 |
| Embeddings | multilingual-e5-small | Qwen3-Embedding-0.6B | MIT / Apache 2.0 |
| Voz → texto | whisper `base` (`small` en Avanzado) | — | MIT |

Por qué Qwen3.5 como base: familia completa de tamaños pequeños (publicada en marzo de 2026), licencia comercial permisiva, buen multilingüe. Gemma 4 (abril de 2026) es fuerte en tamaños “edge” y pasó a Apache 2.0; se compara de igual a igual.

**Antes de fijar la elección (Fase 1/5) se corre un benchmark propio**, porque las tablas públicas no miden lo que necesitamos:
1. Comprensión de 500 descripciones reales en español rioplatense y neutro, con errores de ortografía (“no me anda el wifi”, “la compu se tilda”).
2. Elección correcta de sondas/acciones en JSON restringido.
3. Tasa de alucinación: afirmar algo no presente en los hechos.
4. Velocidad en CPU de gama baja (tokens/s) y RAM pico.
5. Compatibilidad real con la versión de llama.cpp que distribuimos (la arquitectura híbrida de Qwen3.5 debe estar soportada y estable).

Detalle importante: el LLM **no** aprende de informática para el producto; el conocimiento está en la biblioteca y llega por RAG. El modelo solo necesita entender, preguntar y explicar. Por eso alcanzan modelos chicos. Un ajuste fino (LoRA) para el formato de herramientas se evalúa en Fase 5 si el benchmark lo justifica.

---

## 6. Herramientas de diagnóstico y reparación

### 6.1 Fuentes de datos (sondas, todas de solo lectura)

| Área | Fuente | Qué aporta |
|---|---|---|
| CPU | CIM `Win32_Processor`, CPUID, contadores de rendimiento, LibreHardwareMonitor, eventos WHEA-Logger 17/18/19/47 | Modelo, núcleos/hilos, frecuencias, AVX/AVX2, virtualización habilitada en BIOS, temperatura, uso, errores de hardware corregidos/no corregidos |
| GPU | **DXGI** (VRAM real — `Win32_VideoController.AdapterRAM` se corta en 4 GB), registro de controladores, NVML, ADLX, eventos `Display 4101` (TDR), nivel DirectX, versión Vulkan (loader), OpenGL | Integrada vs dedicada, **monitor conectado a la integrada en vez de la dedicada**, controlador y antigüedad, temperatura y uso |
| RAM | SMBIOS (`Win32_PhysicalMemory`, `Win32_PhysicalMemoryArray`), contadores | Módulos, ranuras, tipo DDR, velocidad configurada vs nominal, **canal simple vs doble**, capacidad máxima informada por la BIOS (marcada como “puede ser inexacta”) |
| Almacenamiento | CIM `MSFT_PhysicalDisk` + `MSFT_StorageReliabilityCounter`, smartctl, eventos `Disk 7/51/153`, `Ntfs 55`, prueba de lectura propia | Tipo (HDD/SSD/NVMe/eMMC), SMART, temperatura, desgaste, errores, velocidad, espacio, archivos/carpetas más grandes |
| Placa / BIOS | SMBIOS, `Win32_BaseBoard`, `Win32_BIOS`, registro | Fabricante, modelo, versión BIOS/UEFI, modo UEFI/Legacy, Secure Boot, TPM, **certificados Secure Boot 2023 presentes o no** |
| Energía / térmica | LibreHardwareMonitor, `Kernel-Power 41`, `powercfg /batteryreport /xml`, `/energy`, `/sleepstudy`, `/lastwake`, `/requests` | Apagados inesperados, batería (capacidad de diseño vs actual, ciclos), qué despierta la PC, qué impide suspender |
| Dispositivos | **CfgMgr32** (código de error de cada dispositivo: 10, 28, 43, 45…), SetupAPI, `pnputil /enum-drivers` | Dispositivos con problema, sin controlador, deshabilitados |
| Windows | Versión/compilación/edición, fin de soporte, activación (`SoftwareLicensingProduct` por CIM), WinRE (`reagentc /info`), Event Log, `Win32_ReliabilityRecords`, minidumps (código de bugcheck y módulos, mejor esfuerzo) | Salud del sistema, pantallas azules, actualizaciones fallidas con código |
| Arranque | Eventos `Diagnostics-Performance` 100–110 | Duración real del inicio y **qué programa lo hizo más lento** |
| Programas | Registro de desinstalación, Appx, runtimes instalados, `Application Error 1000`, `.NET Runtime 1026`, `Application Hang 1002` | Qué falla y con qué módulo |
| Office | Click-to-Run (`Configuration`), complementos COM, `Normal.dotm`, estado de licencia | Tipo de instalación, canal, versión, complementos problemáticos |
| Red | CIM `MSFT_NetAdapter`, `MSFT_NetIPConfiguration`, Native Wifi API (señal, canal, banda), pruebas ICMP **y** TCP (algunas redes bloquean ping), DNS, sonda NCSI de Microsoft (detecta portal cautivo), proxy WinINET/WinHTTP, `netsh wlan show wlanreport` | Si el problema es la PC, el router o el proveedor |
| Impresoras | `MSFT_Printer`/`Win32_Printer`, servicio Spooler, puertos (WSD vs TCP/IP), cola | Estado real y causa |
| Seguridad | Defender por WMI (`MSFT_MpComputerStatus`, `MSFT_MpThreat`), `SecurityCenter2` (antivirus de terceros), Firewall COM, enumeración de persistencias (Run, servicios, tareas, WMI, IFEO, Winlogon, políticas de navegador, extensiones, proxy/PAC, hosts, certificados raíz no estándar), verificación Authenticode | Amenazas, protecciones desactivadas, secuestros, programas de acceso remoto instalados |
| Juegos | Bibliotecas de Steam (`libraryfolders.vdf`), Epic, GOG, Xbox; preferencias de GPU por app (`UserGpuPreferences`), Game Mode, HAGS, VRR | Juegos instalados, GPU usada, requisitos |

### 6.2 Herramientas de reparación (todas vigentes en Windows 10 22H2 y Windows 11 24H2/25H2)

| Área | Herramientas / acciones |
|---|---|
| Sistema | `sfc /scannow` y `/offbootdir` (rescate) · `DISM /CheckHealth /ScanHealth /RestoreHealth` (con `/Source` desde ISO oficial si no hay Internet) · `DISM /StartComponentCleanup` · `chkdsk /scan`, `/spotfix`, programación de `/f` · reparación de Windows Update (servicios, renombrado de `SoftwareDistribution`/`catroot2`, BITS) · **“Corregir problemas con Windows Update”** (reinstalación de la versión actual conservando archivos, Windows 11) · reparación in-place con la ISO oficial (Windows 10) · `reagentc /enable` · re-registro de apps Appx · `wsreset` |
| Arranque (rescate) | `bcdboot` (en lugar de `bootrec /fixboot`, que falla en muchos UEFI) · `DISM /Image /Cleanup-Image /RevertPendingActions` · recuperación de BitLocker con la clave del usuario (`manage-bde`) |
| Controladores | Windows Update (API WUA, categoría Drivers) · `pnputil` (exportar, agregar, eliminar) · reinstalación limpia desde el instalador oficial del fabricante (dominios fijados + firma verificada) · reversión al paquete exportado |
| Red | Vaciar DNS · renovar IP · reiniciar adaptador · `netsh winsock reset` · `netsh int ip reset` · quitar proxy · DNS automático/manual · desactivar ahorro de energía del adaptador · reinstalar controlador · olvidar y reconectar Wi-Fi (la clave la escribe el usuario en la ventana de Windows) |
| Impresión | Reiniciar Spooler · vaciar cola (avisando que se pierden trabajos pendientes) · reinstalar controlador · cambiar puerto WSD→TCP/IP · impresora predeterminada · página de prueba |
| Office | Reparación rápida y en línea (`OfficeClickToRun.exe scenario=Repair`) · modo seguro · desactivar complementos · renovar `Normal.dotm` (renombrando) |
| Runtimes | Instalación desde `/runtimes` de VC++, DirectX, .NET, Java |
| Rendimiento | Programas de inicio (mismo mecanismo que el Administrador de tareas, reversible) · plan de energía · liberación de espacio segura · Storage Sense · efectos visuales |
| Seguridad | Escaneo rápido/completo/personalizado de Defender · actualización de firmas (con o sin Internet) · **Defender Offline** (reinicia a WinRE y escanea) · ClamAV + YARA · cuarentena · reparación de secuestros de navegador, proxy, hosts, políticas · desinstalación guiada de acceso remoto no reconocido |
| Hardware | Diagnóstico de memoria de Windows (`mdsched`, lectura automática del resultado tras reiniciar) · Memtest86+ desde el arranque · prueba de CPU acotada con corte por temperatura · prueba de lectura de disco (solo si SMART no indica falla) |
| Archivos | Papelera, Historial de archivos, versiones anteriores (instantáneas), papelera de OneDrive · PhotoRec/TestDisk (en rescate o sobre otro disco) · `attrib` para el “virus de acceso directo” en pendrives · 7-Zip |

### 6.3 Lo que **no** se usa (obsoleto, retirado o riesgoso)

| No usar | Motivo | Reemplazo |
|---|---|---|
| **WMIC** | Retirado; ya no viene instalado en Windows 11 24H2+ | CIM (.NET `Microsoft.Management.Infrastructure`) |
| Solucionadores `msdt` | Retirados por Microsoft | Acciones propias + app “Obtener ayuda” como referencia |
| SaRA (Support and Recovery Assistant) | En retiro | Acciones propias de Office |
| VBScript (`ospp.vbs`, `slmgr.vbs` vía cscript) | En desuso, en proceso de eliminación | CIM `SoftwareLicensingProduct` |
| PowerShell 2.0 | Eliminado de Windows 11 | PowerShell 5.1 solo donde no haya API |
| `bootrec /fixboot` | “Acceso denegado” frecuente en UEFI | `bcdboot` |
| `netsh firewall` | Obsoleto | API COM de Firewall / `advfirewall` |
| WinRing0 | Vulnerable; Defender lo bloquea | PawnIO |
| “Limpiadores de registro”, “liberadores de RAM”, desactivar servicios en masa, borrar el archivo de paginación, desactivar Defender para “ganar rendimiento” | Mitos: no mejoran o rompen el sistema | Diagnóstico real de la causa |
| Sitios de “descarga de drivers” de terceros y “actualizadores de drivers” | Fuente frecuente de malware y PUPs | Windows Update y fabricantes oficiales |

Esta lista se revisa en cada versión contra los anuncios de funciones en desuso de Microsoft.

---

## 7. Licencias y restricciones de distribución

| Componente | Licencia | Obligaciones / restricción |
|---|---|---|
| .NET 10, Avalonia, llama.cpp, whisper.cpp, ONNX Runtime | MIT | Incluir avisos de licencia. |
| SQLite | Dominio público | — |
| sqlite-vec | MIT / Apache 2.0 | Avisos. |
| Qwen3.5, Gemma 4 (según fuentes disponibles) | Apache 2.0 | Avisos; confirmar en la ficha oficial de cada modelo antes de distribuir. |
| multilingual-e5-small, modelos whisper | MIT | Avisos. |
| LibreHardwareMonitorLib | MPL-2.0 | Si modificamos sus archivos, publicar esos archivos. |
| PawnIO | **Verificar en Fase 1** | Redistribución del instalador y del controlador firmado. |
| smartmontools, ClamAV, TestDisk/PhotoRec, ddrescue, Memtest86+, kernel Linux, GRUB | GPL | Se distribuyen como **programas separados** (agregación, no se enlazan con nuestro código). Obligación: ofrecer el código fuente exacto de cada versión distribuida (se publica en un repositorio espejo). |
| Paquetes de Debian | Libres (varias) | Mismo esquema; publicar fuentes. |
| YARA | BSD-3 | Avisos. Las **reglas** YARA de la comunidad tienen licencias propias (algunas no comerciales): se eligen solo las compatibles. |
| 7-Zip | LGPL + restricción unRAR | Redistribuible; no se puede usar el código unRAR para crear un compresor RAR. |
| SumatraPDF | GPL-3 | Programa separado + fuentes. |
| Visual C++ Redistributable, DirectX End-User Runtime, .NET Framework | Redistribuibles de Microsoft | Distribuir los instaladores sin modificar. |
| Eclipse Temurin (Java) | GPL-2 + Classpath Exception | Redistribuible. |
| **Windows PE (ADK)** | Términos de Microsoft | **No se redistribuye**. Restricciones conocidas para distribución comercial a terceros. Alternativas: WinRE de la propia PC (Modo B) y WinPE construido por el técnico con su propio ADK (Modo D). |
| **ISO de Windows** | Microsoft | No se incluye. El usuario la descarga desde Microsoft con la herramienta oficial. |
| Definiciones de Defender | Microsoft | No se reempaquetan: el actualizador las descarga de Microsoft al pendrive del usuario y verifica la firma. |
| Escáneres gratuitos de terceros (Malwarebytes, ESET, Kaspersky, Emsisoft) y herramientas Sysinternals | Propietarias | **No redistribuibles** → solo enlaces oficiales. Por eso la enumeración de persistencias es propia. |
| Controladores de red de fabricantes | Varía | Verificar uno por uno; si alguno no lo permite, se reemplaza por el flujo “descargar desde otra PC”. |
| Datos de requisitos de juegos | — | PCGamingWiki es CC BY-NC-SA (**no comercial**): no se usa si el producto se vende. Se arma una base propia con requisitos oficiales publicados por cada editor (son datos fácticos) y se consulta la tienda de Steam solo a pedido del usuario cuando hay Internet. |
| Documentación de Microsoft Learn | Gran parte CC BY 4.0 | Se puede citar/adaptar con atribución; se verifica repositorio por repositorio. |
| Marcas (Windows, Office, NVIDIA, Steam…) | Marcas registradas | Uso solo descriptivo (“compatible con…”), nunca en logo ni nombre del producto. |

**Licencia del proyecto (decidido):** PC Doctor AI es **código abierto bajo GPL-3.0**. Cualquiera puede usarlo, modificarlo y venderlo, pero quien distribuya una versión modificada debe publicar su código con la misma licencia. Todas las dependencias actuales (MIT, Apache-2.0, BSD, dominio público) son compatibles; ver `THIRD-PARTY-NOTICES.md`.

**Firma de código, a costo cero:** se solicita la firma gratuita para proyectos de código abierto (SignPath Foundation). Mientras tanto, el programa funciona sin firma en la mayoría de las PCs: el aviso de SmartScreen solo aparece con archivos descargados de Internet, no con los copiados al pendrive por quien lo arma. Riesgos sin firma: el Control inteligente de aplicaciones (activo en algunas instalaciones nuevas de Windows 11) y más falsos positivos de antivirus. Cada versión se envía a Microsoft para análisis preventivo de falsos positivos (gratis).

---

## 8. Diseño de la biblioteca de conocimientos

### 8.1 Ficha de un problema (fuente YAML, una por archivo)

```yaml
id: NET-DNS-001
version: 3
categoria: internet/dns
titulo:
  sencillo: "Hay Internet pero las páginas no abren"
  tecnico: "Resolución DNS fallando con conectividad IP operativa"
frases:            # cómo lo dice la gente; alimentan el clasificador
  - "no me abren las páginas"
  - "WhatsApp anda pero Google no"
  - "dice que no se encuentra la dirección del servidor"
sintomas: [paginas_no_cargan, error_dns_navegador]
causas:
  - { id: dns_inalcanzable, prior: 0.40 }
  - { id: dns_manual_incorrecto, prior: 0.25 }
  - { id: proxy_malicioso, prior: 0.10, ver_tambien: SEC-HIJ-004 }
diagnostico:
  hechos: [red.ping_puerta_enlace, red.tcp_1_1_1_1_443, red.resolver_dns, red.config_dns]
  reglas:
    - causa: dns_inalcanzable
      si: { all: [ "red.ping_puerta_enlace.ok", "red.tcp_1_1_1_1_443.ok", "not red.resolver_dns.ok" ] }
      estado: problema_detectado
herramientas: [red.resolver_dns, red.config_dns]
reparacion:        # en orden de menor a mayor riesgo
  - accion: net.dns.flush
  - accion: net.dns.set_dhcp
  - accion: net.dns.set_publico     # pregunta cuál: Cloudflare, Google, Quad9
verificacion: { sonda: red.resolver_dns, esperar: ok }
reversion: automatica_por_snapshot
riesgo: amarillo
permisos: administrador
conexion: no_requerida
compatibilidad: { windows: ["10 22H2", "11 23H2+"], arquitecturas: [x64, arm64, x86] }
madurez: T2
fuentes:
  - https://learn.microsoft.com/...      # siempre fuentes verificables
pruebas:
  falla: lab/faults/net/dns_servidor_inexistente.ps1
  esperado: { top3_incluye: NET-DNS-001, reparacion_ok: true, colaterales: 0 }
revisado: { por: "equipo-redes", fecha: 2026-11-01 }
```

### 8.2 Compilación y búsqueda

- El repositorio `knowledge/` se valida contra un **JSON Schema** en cada cambio (IDs únicos, acciones existentes en el catálogo, reglas sobre hechos que existen, fuentes presentes, prueba de laboratorio declarada para T1+).
- Un compilador genera `knowledge.db` (SQLite) con: tablas normalizadas, índice FTS5 (con normalización de acentos y sinónimos propios), vectores de las frases y descripciones (sqlite-vec) y el AST de las reglas. Se firma.
- La búsqueda de un caso combina: **(a)** coincidencia de reglas con los hechos reales de la PC (lo más fuerte), **(b)** similitud semántica de lo que escribió el usuario, **(c)** BM25 por palabras clave. El resultado es una lista ordenada de problemas candidatos con su evidencia.
- Textos en dos registros (**sencillo** y **técnico**) e idiomas (es-AR por defecto, español neutro, inglés más adelante). **Paquetes regionales** opcionales (por ejemplo, trámites y certificados de organismos de Argentina) que se actualizan aparte.

### 8.3 Cómo se llega a 1.000+ problemas sin que sean genéricos

1. **Taxonomía cerrada** de 21 módulos con metas por módulo (ver documento 02).
2. **Fuentes primarias:** documentación oficial de Microsoft y fabricantes, códigos de error documentados, eventos reales. Se permite usar IA para el **borrador**, pero cada ficha la revisa un técnico, y ninguna sube de T0 sin prueba.
3. **Frases reales:** se recolectan (con permiso) de foros de ayuda, de pruebas con usuarios y de técnicos; mínimo 5 por problema.
4. **Laboratorio:** cada problema con reparación automática tiene su script de inyección de falla.
5. **Ritmo realista:** 1.000 fichas T0 son alcanzables en la Fase 6; llevar todas a T2 es un trabajo continuo que se prioriza por frecuencia (los 200 problemas más comunes primero).

---

## 9. Sistema de actualizaciones

### 9.1 Paquetes

`app` · `rescate` · `motores` · `modelo-<nombre>` · `biblioteca` · `datos` · `clamav` · `defender-defs` · `runtimes` · `herramientas` · `drivers`. Cada uno con versión propia y versión mínima de la app requerida.

### 9.2 Seguridad (esquema inspirado en TUF)

- **Clave raíz** guardada fuera de línea (llave de hardware); firma las claves de los demás roles.
- **Clave de contenido** firma los manifiestos de cada paquete (lista de archivos con SHA-256 y tamaño).
- **Clave de marca de tiempo** (en línea, de vida corta) firma “cuál es la última versión” con vencimiento → impide que un atacante sirva una versión vieja vulnerable (*rollback*) o congele las actualizaciones (*freeze*).
- Firmas Ed25519, descarga por HTTPS, verificación por archivo y por bloque. Nada se activa sin verificar.
- Las definiciones de Defender y los instaladores de terceros se verifican además con su firma Authenticode del fabricante.

### 9.3 Mecánica

| Requisito | Implementación |
|---|---|
| Incremental | Solo se bajan archivos cuyo hash cambió; binarios y biblioteca con **deltas zstd** desde las últimas N versiones. ClamAV usa sus propios diferenciales (`.cdiff`) vía `freshclam` apuntando al pendrive. |
| Reanudable | Descarga por bloques de 8 MB con `Range`; se retoma donde quedó. |
| Control de espacio | Antes de bajar, calcula espacio en el pendrive; si no alcanza, usa como preparación el disco de la PC donde se actualiza (si el usuario lo acepta) o pide liberar espacio. |
| Atomicidad | Se descarga en `/updates/staging`, se verifica, y se activa cambiando un archivo puntero (escritura temporal + renombrado). Si se desenchufa a mitad de camino, al iniciar se detecta y se vuelve al estado anterior. |
| Reversión | Se conserva la versión anterior de app, biblioteca y datos (son chicas). Los modelos no se duplican por espacio: si un modelo nuevo da problemas, se vuelve a bajar el anterior. |
| Sin Internet en la PC rota | **Actualizar desde cualquier otra PC**: el pendrive se conecta a una PC con Internet, se abre PC Doctor y se elige “Actualizar pendrive”. |
| Privacidad | La consulta de versión no envía identificadores, solo versiones. Sin telemetría. Informe anónimo de “la reparación funcionó/no funcionó” **solo si el usuario lo activa**, mostrando antes exactamente qué se envía. |
| Gratuidad | Sin suscripción y **sin costo para el proyecto**: los paquetes se publican en GitHub Releases del repositorio público; los modelos de IA se descargan directamente de Hugging Face (repositorios oficiales de cada modelo, verificados por hash). |

Frecuencias objetivo: firmas ClamAV diarias · definiciones Defender cada vez que se actualiza · biblioteca semanal · app mensual · modelos cuando un benchmark muestra mejora real.

---

## 10. Plan de implementación

Las fases siguen tu orden, con dos cambios deliberados: **el laboratorio de pruebas nace en la Fase 2** (no al final, porque ningún módulo se da por terminado sin pruebas) y **la biblioteca se escribe en paralelo desde la Fase 3**. Duraciones orientativas para un equipo chico (2–3 personas); se recalculan al cerrar la Fase 1.

| Fase | Entregables | Criterio de salida | Duración orientativa |
|---|---|---|---|
| **1. Investigación y arquitectura** | Este documento · pruebas de concepto críticas: llama.cpp desde USB en VM de 4 GB y en USB 2.0; benchmark de modelos en español; flujo de consentimiento de PawnIO; Avalonia en GPU integrada antigua; arranque del Linux firmado con Secure Boot en 5 PCs; herramienta AOT corriendo en WinRE; auditoría de licencias (PawnIO, controladores de red) | Cada prueba de concepto con resultado medido; riesgos actualizados | 3–4 semanas |
| **2. Prototipo ejecutable** | Interfaz base (modo sencillo y técnico) · orquestador · agente elevado + IPC · catálogo con 15 acciones · diario + deshacer · CLI · **laboratorio mínimo con 15 fallas automatizadas** | 15 problemas en T2 en Windows 10 y 11 | 6 semanas |
| **3. Diagnóstico integral** | Todas las sondas de la sección 6.1 · lenguaje de reglas · informe de salud (sencillo/técnico) con los 5 estados | 0 falsos “Problema detectado” en imágenes limpias; diagnóstico completo < 2 min en HDD | 8 semanas |
| **4. Motor de reparación y seguridad** | Respaldos por tipo · reversión · reinicio con verificación · Defender + ClamAV + YARA + persistencias + cuarentena · detección de estafas/acceso remoto · actualizador mínimo de biblioteca | Todas las acciones con prueba de reversión | 8 semanas |
| **5. IA local + biblioteca** | Adaptador llama-server · perfiles automáticos · guardas de salida · clasificador semántico · RAG híbrido · evaluación de 2.000 conversaciones | Aciertos de intención ≥ 90 % en top-3; alucinaciones en veredictos = 0 (las bloquea el motor) | 8 semanas |
| **6. 1.000+ problemas** | Fichas T0 de los 21 módulos; 200 más frecuentes en T2 | Validación de esquema 100 %; T2 en los 200 prioritarios | 12–16 semanas (en paralelo desde la Fase 3) |
| **7. Modo adulto mayor** | Letras grandes, alto contraste, voz de entrada y salida, “Capturar el mensaje de error”, botón ANALIZAR Y SOLUCIONAR | **Pruebas con personas mayores reales**: 8 de 10 resuelven 3 tareas sin ayuda | 4 semanas |
| **8. Juegos, programas, Office, compatibilidad** | Base de requisitos, tabla de rendimiento relativo CPU/GPU, chequeos de anticheat (TPM/Secure Boot), compatibilidad con Windows 11, runtimes | Comparación correcta en 100 juegos de referencia | 6 semanas |
| **9. Actualizaciones** | Esquema completo de la sección 9 | Pruebas de corte de energía/desconexión en cada etapa; ataque de reversión bloqueado | 4 semanas |
| **10. Rescate USB** | Herramienta AOT para WinRE · imagen Debian firmada con la app Linux · Memtest86+ · creador del pendrive con detección de pendrives falsos | Arranca en 20 modelos de PC distintos con Secure Boot activo (documentando los que no) | 8 semanas |
| **11. Pruebas en hardware real** | Matriz de la sección 11 · beta con técnicos | Métricas dentro de objetivo | 6 semanas |
| **12. Optimización, documentación, distribución** | Firma de código, manual, sitio de descargas, imagen para fabricar pendrives | Versión 1.0 | 4 semanas |

**Hito MVP (≈ 6 meses):** Fases 1–5 con 150–200 problemas en T2: ya es un producto útil y real en Windows. **Versión 1.0 (≈ 14–18 meses)** con todo lo anterior.

---

## 11. Pruebas y laboratorio de averías

**Ciclo automático por problema** (todas las noches, en integración continua):
`snapshot de VM → inyectar falla → pcdoctor-cli diagnose → ¿el problema esperado está en el top 3? → fix → verify → comparar estado del sistema (servicios, registro, controladores, red) para detectar colaterales → probar deshacer → restaurar VM`.

**VMs (Hyper-V):** Windows 10 22H2 x64 y x86 · Windows 11 24H2 y 25H2 x64 · Windows 11 ARM64 (en hardware ARM) · configuraciones de 4/8/16/32 GB · disco limitado por QoS para simular HDD · red simulada (DNS roto, proxy, portal cautivo, sin Internet).

**Hardware real mínimo:** notebook Celeron N4020 4 GB eMMC (eMMC no expone SMART: se valida el estado “No se pudo comprobar”) · PC 8 GB HDD + GPU integrada Intel · PC 16 GB SSD + NVIDIA · PC 16 GB NVMe + AMD · PC 32 GB gama alta · notebook Windows 11 con BitLocker activo · equipo Secured-core (CA de terceros deshabilitada por defecto) · PC sin Internet.

**Métricas obligatorias por versión:** RAM y CPU pico y promedio · tiempo hasta la pantalla principal (objetivo < 5 s en SSD, < 15 s en HDD + USB 2.0) · tiempo de diagnóstico completo · tamaño del sistema · **tasa de reparaciones exitosas verificadas** · **falsos diagnósticos** · **errores provocados por las propias reparaciones** (objetivo: 0 en laboratorio).

---

## 12. Riesgos principales

| Riesgo | Mitigación |
|---|---|
| Antivirus que marcan PC Doctor como sospechoso (es una herramienta que modifica el sistema) | Firma de código, envío preventivo a Microsoft y otros fabricantes, sin empaquetadores ofuscados, comportamiento transparente. |
| Certificados de Secure Boot de 2011 vencidos en 2026 | El arranque del rescate usa shim firmado con las CA 2011 **y** 2023; PC Doctor detecta si la PC tiene los certificados 2023 y lo informa. Además, el Modo B (WinRE) no depende de esto. |
| BitLocker activado por defecto en muchas instalaciones de Windows 11 | Antes de sugerir cambios en la BIOS o arrancar desde el USB, PC Doctor verifica si hay BitLocker y **exige confirmar que el usuario tiene la clave de recuperación** (le indica dónde encontrarla en su cuenta Microsoft). |
| PCs Secured-core que no arrancan Linux firmado por defecto | Guía para habilitar la CA de terceros, o usar el Modo B. |
| Anticheats que bloquean controladores de sensores | Sensores opcionales, instalados temporalmente y removibles. |
| Modelos que no entran en PCs débiles | El producto completo funciona sin LLM (decisión de arquitectura, no parche). |
| Licencias de terceros cambiantes | Auditoría por versión; componentes reemplazables por módulo. |
| Volumen de la biblioteca | Priorización por frecuencia; madurez visible; nada se muestra como automático sin T2. |
| Estafas de soporte técnico que usen el nombre del producto | Política pública: PC Doctor nunca llama, nunca pide pagos ni acceso remoto; se muestra en la app. |

---

## 13. Decisiones que necesito de vos

1. **Modelo de negocio:** ✅ decidido — el pendrive **se vende a precio bajo**. No se usa nada con licencia no comercial; la firma de código es obligatoria antes de vender. (Estado de avance en [03-Estado-de-implementacion.md](03-Estado-de-implementacion.md).)
2. **Equipo y plazos:** ¿cuántas personas y con qué perfil? Las duraciones de la sección 10 suponen 2–3.
3. **Carpeta del proyecto:** para empezar la Fase 2 conviene crear el repositorio en una carpeta tuya (por ejemplo `F:\Proyectos\pc-doctor-ai`).
4. **Idiomas iniciales:** propuesta: es-AR + español neutro en la 1.0; inglés después.
5. **Firma de código:** ✅ decidido — firma gratuita para código abierto (el proyecto es GPL-3.0). **Costo cero es un requisito inamovible.**
6. **Permisos:** ✅ decidido — el diagnóstico nunca pide permiso; las reparaciones piden como máximo **un** aviso de Windows por sesión, explicado antes con letra grande. Saltear el aviso de Windows no es posible de forma legítima.

---

## Fuentes consultadas para datos recientes

- Qwen3.5 small (0.8B/2B/4B/9B, marzo 2026): [Gigazine](https://gigazine.net/gsc_news/en/20260303-qwen-3-5-small), [36Kr](https://eu.36kr.com/en/p/3706848726643078), [tokencost.app](https://tokencost.app/blog/qwen-3-5-small-benchmarks-pricing)
- Gemma 4 bajo Apache 2.0 (abril 2026): [Techwyse](https://www.techwyse.com/news/ai-search/google-deepmind-gemma-4-open-models-apache-2-0-release), [NYU Shanghai RITS](https://rits.shanghai.nyu.edu/ai/google-releases-gemma-4-frontier-open-models-under-apache-2-0/), [ApX E2B](https://apxml.com/models/gemma-4-e2b)
- Panorama de modelos pequeños 2026: [DataNorth](https://datanorth.ai/blog/the-best-open-source-llm-in-2026), [Prem AI](https://blog.premai.io/best-lightweight-language-models-worth-running)
- Vencimiento de certificados Secure Boot 2026: [Red Hat](https://www.redhat.com/es/blog/expiration-secure-boot-signing-certificates-2026), [Eclypsium](https://eclypsium.com/blog/microsoft-secure-boot-certificates-expire-2026/), [Google Cloud](https://docs.cloud.google.com/compute/docs/security/ms-secure-boot-certificates-expiration?authuser=0), [LWN](https://lwn.net/Articles/1079808/)
- WinRing0 marcado por Defender / PawnIO: [Neowin](https://www.neowin.net/news/windows-1110-is-flagging-winring0-on-your-pc-monitoring-fan-control-apps-heres-why/), [Slashdot](https://it.slashdot.org/story/25/03/14/1351225/windows-defender-now-flags-winring0-driver-as-security-threat-breaking-multiple-pc-monitoring-tools), [OmenMon-Reborn](https://github.com/seakyy/OmenMon-Reborn/releases/tag/v1.4.0-reborn)
- Restricciones de WinPE: [Microsoft Learn — WinPE USB](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/winpe-create-usb-bootable-drive?view=windows-11), [PassMark](https://forums.passmark.com/burnintest/4434-bootable-burnintest-product-change-notification), [Acronis](https://www.acronis.com/en/support/documentation/AcronisCyberProtect_16/winpe-winre-based-bootable-media.html)
- Windows 10 ESU para consumidores extendido a octubre de 2027: [Help Net Security](https://www.helpnetsecurity.com/2026/06/26/microsoft-windows-10-free-security-updates-esu-program/), [PC-Welt](https://www.pcwelt.de/article/3177497/windows-10-bekommt-ein-weiteres-jahr-lang-updates-sensation.html), [endoflife.ai](https://endoflife.ai/article-windows-10-consumer-esu-drift)

*Todo dato marcado “verificar en Fase 1” se confirma con la fuente oficial antes de programar la parte que depende de él.*
