# PC DOCTOR AI — Catálogo de Problemas (taxonomía v0.1)

**Fecha:** 1 de noviembre de 2026 · Complementa a [01-Arquitectura-y-Plan-Tecnico.md](01-Arquitectura-y-Plan-Tecnico.md)

Este documento define **qué cubre la biblioteca**: 21 módulos (los 13 que pediste + 8 nuevos) con una meta de **1.160 problemas**. Para cada módulo se listan ejemplos representativos con su ID, el tipo de solución y el riesgo. La ficha completa de cada uno sigue el formato de la sección 8.1 del documento de arquitectura.

**Leyenda**
- **AUTO**: diagnostica, repara, verifica y puede deshacer. **GUIADA**: pasos guiados con verificación cuando es posible. **DERIVA**: diagnóstico con evidencia + a quién acudir y qué pedir.
- 🟢 sin riesgo · 🟡 reversible, pide permiso · 🔴 destructivo o irreversible, doble confirmación.
- ★ = agregado por mí, no estaba en tu lista.
- Una entrada marcada AUTO se publica como automática **solo cuando alcanza madurez T2** en el laboratorio; hasta entonces se muestra como GUIADA.

## Metas por módulo

| Módulo | Meta | Módulo | Meta |
|---|---|---|---|
| A. Windows y sistema operativo | 120 | L. Programación | 60 |
| B. Programas y aplicaciones | 100 | M. Uso cotidiano | 60 |
| C. Videojuegos | 80 | ★ N. Arranque, BIOS/UEFI y cifrado | 40 |
| D. Internet y redes | 80 | ★ O. Estafas y protección del usuario | 30 |
| E. Impresoras y periféricos | 70 | ★ P. Notebooks, batería y energía | 35 |
| F. Archivos y almacenamiento | 70 | ★ Q. Teclado, idioma, accesibilidad y configuración básica | 40 |
| G. Audio, video e imagen | 60 | ★ R. Celulares, TV y dispositivos conectados | 30 |
| H. Cuentas y acceso | 40 | ★ S. Copias, migración y reinstalación | 25 |
| I. Seguridad informática | 80 | ★ T. Mejoras de hardware y ciclo de vida | 20 |
| J. Rendimiento | 50 | ★ U. Fallas físicas (diagnóstico y derivación) | 30 |
| K. Compatibilidad | 40 | **Total** | **1.160** |

---

## A. Windows y sistema operativo (120)

- `WIN-LIFE-001` ★ Windows 10 sin soporte: explicar estado real (ESU de consumidores reportado hasta el 12/10/2027, a confirmar), comprobar si la PC admite Windows 11 y opciones — GUIADA 🟢
- `WIN-LIFE-002` ★ Versión de Windows 11 fuera de soporte (por ejemplo 24H2 Home/Pro desde el 13/10/2026) → actualizar a la versión vigente — GUIADA 🟡
- `WIN-LIFE-003` ★ ¿Mi PC puede pasar a Windows 11? (TPM 2.0, Secure Boot, UEFI, CPU en lista oficial, RAM, disco) — AUTO 🟢
- `WIN-UPD-001` Windows Update falla con código 0x8024xxxx / 0x800fxxxx → reparación de componentes — AUTO 🟡
- `WIN-UPD-002` Actualización atascada en un porcentaje — GUIADA 🟡
- `WIN-UPD-003` Bucle de “No se pudieron completar las actualizaciones, deshaciendo cambios” — AUTO 🟡 (rescate)
- `WIN-UPD-004` Falta de espacio para actualizar — AUTO 🟡
- `WIN-SYS-001` Archivos de sistema dañados (SFC/DISM con o sin Internet) — AUTO 🟡
- `WIN-SYS-002` ★ El entorno de recuperación (WinRE) está desactivado → `reagentc /enable` — AUTO 🟡
- `WIN-SYS-003` Servicios esenciales deshabilitados (por “optimizadores”) → restaurar valores por defecto de Microsoft — AUTO 🟡
- `WIN-BSOD-001…040` Pantallas azules por código (IRQL_NOT_LESS_OR_EQUAL, MEMORY_MANAGEMENT, WHEA_UNCORRECTABLE_ERROR, VIDEO_TDR_FAILURE, CRITICAL_PROCESS_DIED…) con módulo culpable cuando el volcado lo permite — AUTO/GUIADA/DERIVA según causa
- `WIN-BOOT-001` Inicio lento: medido con eventos de rendimiento y programa culpable — AUTO 🟡
- `WIN-BOOT-002` ★ “Inicio rápido” provocando fallas (no apaga del todo) — AUTO 🟡
- `WIN-SHL-001` Explorador de archivos congelado / se reinicia (extensiones de shell de terceros) — AUTO 🟡
- `WIN-SHL-002` Menú Inicio o barra de tareas no responde — AUTO 🟡
- `WIN-SHL-003` ★ Búsqueda de Windows no encuentra archivos → reconstruir índice — AUTO 🟡
- `WIN-SHL-004` ★ Miniaturas o íconos incorrectos → reconstruir cachés — AUTO 🟢
- `WIN-PWR-001` La PC no suspende / se despierta sola (dispositivo o tarea programada culpable) — AUTO 🟡
- `WIN-PWR-002` No se apaga o tarda en apagar — AUTO 🟡
- `WIN-PRF-001` Perfil temporal / “No podemos iniciar sesión en tu cuenta” — GUIADA 🟡
- `WIN-PRM-001` Errores de permisos en carpetas del sistema — GUIADA 🟡
- `WIN-STO-001` ★ Microsoft Store no abre o no descarga → `wsreset` y re-registro — AUTO 🟡
- `WIN-ACT-001` Windows no activado: diagnóstico del estado y cómo activar legítimamente (licencia digital tras cambio de hardware, solucionador de activación) — GUIADA 🟢
- `WIN-TIME-001` ★ Hora/fecha incorrecta (pila CMOS, zona horaria, sincronización) — causa errores de certificados — AUTO 🟡 / DERIVA si es la pila

## B. Programas y aplicaciones (100)

- `APP-GEN-001` El programa no abre: falta DLL/runtime → instalar el runtime correcto desde `/runtimes` — AUTO 🟡
- `APP-GEN-002` El programa se cierra al abrir: análisis de `Application Error` y módulo culpable — AUTO/GUIADA
- `APP-GEN-003` Error 0xc000007b (mezcla 32/64 bits de runtimes) — AUTO 🟡
- `APP-GEN-004` Programa que no se puede desinstalar → desinstalador oficial / limpieza de entrada huérfana — AUTO 🟡
- `APP-GEN-005` Instalador falla (MSI 1603, servicio Windows Installer) — AUTO 🟡
- `APP-GEN-006` ★ “Esta aplicación no se puede ejecutar en el equipo” (arquitectura incorrecta, ARM64 vs x64) — GUIADA 🟢
- `APP-GEN-007` ★ SmartScreen o Defender bloquea un programa legítimo → verificar firma y origen antes de cualquier excepción; nunca desactivar protección — GUIADA 🟢
- `APP-OFF-001` Word/Excel/PowerPoint no abre → modo seguro, complementos, reparación rápida/en línea — AUTO 🟡
- `APP-OFF-002` Office pide activar / “Producto sin licencia” → estado real y pasos legítimos — GUIADA 🟢
- `APP-OFF-003` Excel lento con archivos grandes / cálculo automático — GUIADA 🟢
- `APP-OFF-004` Documento dañado → recuperar con “Abrir y reparar”, versiones de OneDrive, autoguardado — GUIADA 🟢
- `APP-OFF-005` ★ Word siempre abre en “modo de compatibilidad” o con otra plantilla (`Normal.dotm`) — AUTO 🟡
- `APP-OUT-001` Outlook no abre / se cuelga → perfil, archivo OST, complementos — AUTO 🟡
- `APP-OUT-002` Buzón lleno / PST demasiado grande — GUIADA 🟢
- `APP-PDF-001` Los PDF se abren con el programa equivocado → app predeterminada — AUTO 🟡
- `APP-BRW-001` Navegador lento → extensiones, caché, perfil — AUTO 🟡
- `APP-BRW-002` Navegador no abre / se cierra → perfil dañado (con copia previa del perfil) — AUTO 🟡
- `APP-NET-001` Programa pide .NET Framework 3.5 / 4.x — AUTO 🟡
- `APP-JAV-001` Programa pide Java (incluidos aplicativos de organismos) — AUTO 🟡

## C. Videojuegos (80)

- `GAM-REQ-001` ¿Corre este juego en mi PC? (mínimos/recomendados vs hardware detectado) — AUTO 🟢
- `GAM-REQ-002` ★ El juego exige **AVX/AVX2** y el procesador no lo tiene — AUTO 🟢 (diagnóstico definitivo)
- `GAM-REQ-003` ★ El juego exige SSD o espacio que no hay — AUTO 🟢
- `GAM-AC-001` ★ Anticheat exige **TPM 2.0 / Secure Boot** (Valorant, Battlefield 6, Call of Duty…) → verificar y guiar en BIOS con aviso de BitLocker — GUIADA 🟡
- `GAM-AC-002` ★ Anticheat (EAC, BattlEye, Vanguard, FACEIT) falla por controladores incompatibles / Integridad de memoria — GUIADA 🟡
- `GAM-GPU-001` ★ El juego corre en la **GPU integrada** en vez de la dedicada (notebooks) → preferencia de GPU por app — AUTO 🟡
- `GAM-GPU-002` ★ El monitor está conectado a la placa madre y no a la placa de video — GUIADA 🟢 (detectado por DXGI)
- `GAM-GPU-003` Controlador de video antiguo o dañado → instalación limpia oficial con copia del anterior — AUTO 🟡
- `GAM-DSP-001` ★ Monitor de 144/165 Hz funcionando a 60 Hz — AUTO 🟡
- `GAM-DSP-002` Problemas de pantalla completa / resolución — GUIADA 🟡
- `GAM-FPS-001` FPS bajos: plan de energía, notebook con batería, temperatura, RAM en canal simple, RAM por debajo de su velocidad nominal — AUTO/GUIADA
- `GAM-FPS-002` Stuttering: caché de sombreadores, procesos en segundo plano, disco — GUIADA 🟡
- `GAM-DX-001` Error de DirectX / falta d3dx9_43.dll, xinput1_3.dll → DirectX End-User Runtime — AUTO 🟡
- `GAM-VK-001` Error de Vulkan / OpenGL → controlador — AUTO 🟡
- `GAM-STM-001` Steam: verificar archivos del juego (`steam://validate/<appid>`), descarga trabada, caché — AUTO 🟢
- `GAM-XBX-001` Xbox App / Game Pass no instala (servicios de juegos, Store) — AUTO 🟡
- `GAM-PAD-001` Mando/joystick no reconocido (Xbox, PlayStation, genéricos) — AUTO/GUIADA
- `GAM-NET-001` Multijugador: NAT estricto, firewall, puertos (sin abrir puertos sin permiso) — GUIADA 🟡

## D. Internet y redes (80)

- `NET-TRI-001` Triage: ¿falla la PC, el router o el proveedor? (adaptador → puerta de enlace → IP pública → DNS → web) — AUTO 🟢
- `NET-DNS-001` Hay Internet pero las páginas no abren (DNS) — AUTO 🟡
- `NET-DHCP-001` Dirección 169.254.x.x (no obtiene IP) — AUTO 🟡
- `NET-PRX-001` Proxy configurado sin querer o por malware — AUTO 🟡
- `NET-WIFI-001` Wi-Fi se desconecta solo (ahorro de energía del adaptador, banda, canal) — AUTO 🟡
- `NET-WIFI-002` No aparece ninguna red Wi-Fi (adaptador deshabilitado, modo avión, tecla Fn, controlador) — AUTO 🟡
- `NET-WIFI-003` ★ Señal débil: medición e indicación de mejor ubicación/banda — GUIADA 🟢
- `NET-ETH-001` Ethernet “cable desconectado” — GUIADA/DERIVA
- `NET-DRV-001` ★ Sin controlador de red después de reinstalar Windows → paquete de controladores del pendrive — AUTO 🟡
- `NET-STK-001` Pila TCP/IP o Winsock dañada — AUTO 🟡 (reinicio)
- `NET-VPN-001` VPN que dejó la red rota (adaptadores virtuales, rutas) — GUIADA 🟡
- `NET-CRT-001` Errores de certificado en todos los sitios (hora incorrecta, antivirus que intercepta HTTPS, raíz maliciosa) — AUTO/GUIADA
- `NET-CAP-001` ★ Portal cautivo (Wi-Fi de bar/hotel) que no aparece — AUTO 🟢
- `NET-SHR-001` Compartir carpetas o impresoras en red local — GUIADA 🟡
- `NET-SPD-001` Internet lento: medición, comparación con lo contratado, Wi-Fi vs cable — GUIADA 🟢
- `NET-BT-001` Bluetooth no aparece / no empareja — AUTO/GUIADA

## E. Impresoras y periféricos (70)

- `PRN-SPL-001` Cola de impresión trabada — AUTO 🟡
- `PRN-OFF-001` Impresora “sin conexión” (puerto WSD, IP cambiada, servicio) — AUTO 🟡
- `PRN-DRV-001` Controlador incorrecto o dañado — AUTO 🟡
- `PRN-DEF-001` ★ Imprime en otra impresora (Windows administra la predeterminada) — AUTO 🟡
- `PRN-BLK-001` Imprime en blanco / manchado (cabezales, tinta, prueba de inyectores) — GUIADA / DERIVA
- `PRN-SCN-001` El escáner no funciona / escanear a PDF — GUIADA 🟡
- `PER-USB-001` “Dispositivo USB no reconocido” (descriptor, energía, suspensión selectiva) — AUTO 🟡
- `PER-DEV-001` Dispositivo con código de error del Administrador de dispositivos (10, 28, 43, 45…) — AUTO/GUIADA según código
- `PER-KBD-001` Teclado no escribe / escribe solo — GUIADA
- `PER-MOU-001` ★ Desapareció el puntero / el touchpad no responde (tecla Fn, deshabilitado en configuración) — AUTO 🟡
- `PER-CAM-001` Webcam no detectada (privacidad de Windows, tapa física, tecla Fn, controlador) — AUTO/GUIADA
- `PER-MIC-001` Micrófono no funciona (permisos de privacidad, dispositivo predeterminado) — AUTO 🟡
- `PER-DSK-001` Disco externo no aparece (sin letra, sin formato, energía) — AUTO 🟡 / 🔴 si requiere formatear

## F. Archivos y almacenamiento (70)

- `FIL-SPC-001` Disco lleno: análisis de qué ocupa y limpieza segura (temporales, caché de Windows Update, `Windows.old` con aviso, papelera con permiso) — AUTO 🟡
- `FIL-SPC-002` ★ Archivo de hibernación / puntos de restauración ocupando mucho — AUTO 🟡
- `FIL-DEL-001` “Borré un archivo”: papelera → Historial de archivos → versiones anteriores → papelera de OneDrive → recuperación (sin escribir en el disco afectado) — GUIADA 🟢
- `FIL-COR-001` Archivo dañado (documento, ZIP, PDF) — GUIADA 🟢
- `FIL-ZIP-001` No puedo abrir RAR/7z/ZIP — AUTO 🟢
- `FIL-DUP-001` Archivos duplicados (detectar; borrar **solo** con selección del usuario) — GUIADA 🟡
- `FIL-ACC-001` Carpeta inaccesible / “Acceso denegado” en un disco de otra PC — GUIADA 🟡
- `FIL-USB-001` ★ Pendrive muestra “vacío” y solo hay accesos directos (virus de acceso directo) → limpiar y recuperar archivos ocultos — AUTO 🟡
- `FIL-USB-002` ★ Pendrive protegido contra escritura / en RAW — GUIADA 🟡/🔴
- `FIL-USB-003` ★ ¿Mi pendrive o disco es falso? (capacidad real) — AUTO 🟢
- `FIL-ODR-001` OneDrive no sincroniza / conflicto de archivos — AUTO/GUIADA
- `FIL-ODR-002` ★ “OneDrive me movió el Escritorio y los Documentos” (copia de seguridad de carpetas) — GUIADA 🟡
- `FIL-ODR-003` ★ Almacenamiento de OneDrive lleno (5 GB gratuitos) — GUIADA 🟢
- `FIL-DSK-001` Disco no reconocido / partición perdida (TestDisk, en modo rescate) — GUIADA 🔴
- `FIL-BAK-001` Configurar copia de seguridad automática — GUIADA 🟡

## G. Audio, video e imagen (60)

- `AUD-001` No hay sonido (dispositivo predeterminado, silencio por app, servicio de audio, controlador) — AUTO 🟡
- `AUD-002` ★ El sonido sale por el monitor/TV HDMI en vez de los parlantes (o al revés) — AUTO 🟡
- `AUD-003` ★ Auriculares Bluetooth suenan mal en videollamadas (perfil manos libres vs estéreo) — GUIADA 🟢
- `AUD-004` Micrófono con eco, muy bajo o con ruido — GUIADA 🟡
- `VID-001` Videos trabados o sin imagen (códecs, aceleración por hardware, controlador) — AUTO/GUIADA
- `VID-002` ★ Videos HEVC/H.265 no se reproducen → opciones legítimas (extensión oficial o reproductor libre) — GUIADA 🟢
- `VID-003` Grabar la pantalla (Recortes, barra de juegos, OBS) — GUIADA 🟢
- `VID-004` Exportación de video falla / muy lenta (GPU no usada, espacio, códec) — GUIADA 🟡
- `DSP-001` Resolución incorrecta o imagen borrosa (escalado) — AUTO 🟡
- `DSP-002` ★ Pantalla dada vuelta / al costado — AUTO 🟢
- `DSP-003` ★ HDR que se ve “lavado” — GUIADA 🟡
- `DSP-004` Segundo monitor no detectado / duplicar o extender — AUTO/GUIADA

## H. Cuentas y acceso (40)

- `ACC-PWD-001` Olvidé la contraseña de Windows: cuenta Microsoft (recuperación oficial en línea), cuenta local (preguntas de seguridad, otra cuenta administradora, disco de restablecimiento). **Nunca** se saltea el control — GUIADA 🟢
- `ACC-PIN-001` ★ El PIN de Windows Hello no funciona → restablecer solo el PIN — GUIADA 🟡
- `ACC-MSA-001` Cuenta Microsoft bloqueada / verificación de identidad — DERIVA (soporte oficial)
- `ACC-2FA-001` Perdí el celular con la verificación en dos pasos → códigos de recuperación y proceso oficial — DERIVA
- `ACC-LOC-001` ★ Pasar de cuenta Microsoft a cuenta local o al revés — GUIADA 🟡
- `ACC-PRF-001` Perfil de usuario dañado → nuevo perfil y migración de archivos — GUIADA 🔴
- `ACC-ADM-001` ★ “Necesitás permisos de administrador” y el usuario no es administrador — GUIADA 🟢

## I. Seguridad informática (80)

- `SEC-SCN-001` Análisis completo (Defender + ClamAV/YARA si hay memoria) con informe honesto: “no se detectaron amenazas con estos motores y firmas del día X”, nunca “PC 100 % limpia” — AUTO 🟢
- `SEC-SCN-002` Análisis sin conexión (Defender Offline / rescate Linux) — AUTO 🟡
- `SEC-AV-001` Antivirus desactivado o desactualizado → reactivar y actualizar — AUTO 🟡
- `SEC-AV-002` ★ Dos antivirus en conflicto (y antivirus de prueba vencidos que siguen instalados) → herramienta de desinstalación oficial del fabricante — GUIADA 🟡
- `SEC-FW-001` Firewall desactivado — AUTO 🟡
- `SEC-HIJ-001` Secuestro del navegador (buscador, página de inicio, “administrado por tu organización” falso) — AUTO 🟡
- `SEC-HIJ-002` Extensiones maliciosas — AUTO 🟡
- `SEC-HIJ-003` ★ Notificaciones publicitarias de sitios web (spam en la esquina de la pantalla) — AUTO 🟡
- `SEC-HIJ-004` Proxy, archivo hosts o DNS alterados — AUTO 🟡
- `SEC-PER-001` Programas de inicio, tareas o servicios desconocidos (sin firma, en carpetas temporales) — GUIADA 🟡
- `SEC-CRT-001` ★ Certificado raíz no estándar instalado (intercepción de tráfico) — GUIADA 🟡
- `SEC-RAN-001` Ransomware: aislar de la red, identificar la familia, preservar archivos cifrados, buscar descifrador público oficial (No More Ransom), no pagar — GUIADA/DERIVA
- `SEC-PUP-001` Programas potencialmente no deseados (“optimizadores”, “actualizadores de drivers”) — AUTO 🟡
- `SEC-PRV-001` Privacidad: permisos de cámara, micrófono, ubicación por app; ID de publicidad — AUTO 🟡
- `SEC-QUA-001` Cuarentena y restauración de un falso positivo — AUTO 🟡

## J. Rendimiento (50)

- `PERF-CPU-001` CPU al 100 % (proceso culpable y por qué) — AUTO/GUIADA
- `PERF-RAM-001` RAM llena (qué la usa; si falta RAM física, decirlo) — GUIADA 🟢
- `PERF-DSK-001` Disco al 100 % en HDD (indexado, actualizaciones, antivirus, disco fallando) — AUTO/GUIADA
- `PERF-STA-001` Demasiados programas al inicio — AUTO 🟡
- `PERF-THR-001` ★ La PC baja la velocidad por temperatura (thermal throttling) — DERIVA (limpieza física) + GUIADA
- `PERF-PWR-001` Plan de energía en ahorro / notebook limitada en batería — AUTO 🟡
- `PERF-BGR-001` ★ Procesos que vuelven a arrancar solos (actualizadores, telemetría de terceros) — GUIADA 🟡
- `PERF-MYTH-001` ★ “Optimizador” de terceros que rompió servicios → restaurar valores de Microsoft — AUTO 🟡

## K. Compatibilidad (40)

- `CMP-W11-001` Compatibilidad con Windows 11 y qué falta exactamente — AUTO 🟢
- `CMP-W11-002` ★ Disco MBR que impide Secure Boot → conversión `mbr2gpt` (con copia previa) — GUIADA 🔴
- `CMP-SW-001` Programa antiguo no corre → modo de compatibilidad, runtimes, alternativas — AUTO/GUIADA
- `CMP-ARM-001` ★ PC con procesador ARM (Snapdragon): qué programas/juegos/controladores no son compatibles — AUTO 🟢
- `CMP-RAM-001` ¿Qué RAM le puedo agregar? (tipo, ranuras libres, máximo informado) — AUTO 🟢
- `CMP-MON-001` ¿Mi PC soporta este monitor? (salidas, versiones HDMI/DP, resolución y Hz máximos) — GUIADA 🟢
- `CMP-DEV-001` Dispositivo sin controlador para Windows 11 — GUIADA 🟢

## L. Programación (60)

- `DEV-PATH-001` “python/node/git no se reconoce como comando” (PATH, alias de ejecución de la Store) — AUTO 🟡
- `DEV-PATH-002` ★ PATH demasiado largo o dañado — AUTO 🟡
- `DEV-LONG-001` ★ Rutas largas deshabilitadas (`LongPathsEnabled`) — AUTO 🟡
- `DEV-PS-001` ★ “La ejecución de scripts está deshabilitada” (política de ejecución, solo para el usuario actual) — AUTO 🟡
- `DEV-PORT-001` ★ “Puerto en uso”: qué proceso lo ocupa — AUTO 🟢
- `DEV-PY-001` pip con error SSL / entornos virtuales rotos — GUIADA 🟡
- `DEV-NODE-001` node-gyp necesita herramientas de compilación — GUIADA 🟡
- `DEV-GIT-001` Credenciales de Git, finales de línea, certificados — GUIADA 🟡
- `DEV-VS-001` Visual Studio / Build Tools dañados → reparación del instalador — AUTO 🟡
- `DEV-VIRT-001` ★ Virtualización deshabilitada en BIOS (WSL2, Docker, Android Studio) — GUIADA 🟡
- `DEV-WSL-001` WSL no arranca — AUTO/GUIADA
- `DEV-DOCK-001` Docker Desktop no inicia (WSL2/Hyper-V, conflicto con VirtualBox) — GUIADA 🟡
- `DEV-UE-001` Unreal/Unity: falta de SDK, VC++, espacio, controladores de GPU — GUIADA 🟡
- `DEV-DB-001` Servicio de MySQL/PostgreSQL no inicia — GUIADA 🟡

## M. Uso cotidiano (60)

- `DAY-MAIL-001` No puedo enviar o recibir correo (configuración, contraseña de aplicación, buzón lleno) — GUIADA 🟢
- `DAY-VC-001` Zoom/Meet/Teams: no me ven o no me escuchan (permisos del navegador y de Windows) — AUTO 🟡
- `DAY-PDF-001` Unir, firmar o rellenar un PDF — GUIADA 🟢
- `DAY-SIG-001` Firma digital / token USB no reconocido (controlador del token, certificado, hora) — GUIADA 🟡
- `DAY-GOV-001` ★ Trámites en organismos públicos: navegador recomendado, Java, certificados (paquetes regionales, por ejemplo Argentina) — GUIADA 🟢
- `DAY-BNK-001` La app/web del banco no funciona: hora, certificados, extensiones, navegador. **Nunca** se piden ni guardan claves — GUIADA 🟢
- `DAY-DWN-001` ★ “No encuentro lo que descargué” — AUTO 🟢
- `DAY-DEF-001` ★ Cambiar el programa con el que se abre un tipo de archivo — AUTO 🟡
- `DAY-CLP-001` ★ Copiar y pegar dejó de funcionar — AUTO 🟡

---

## ★ N. Arranque, BIOS/UEFI y cifrado (40)

- `BOOT-001` Windows no inicia: “Automatic Repair” en bucle → Modo B (WinRE) — AUTO 🟡
- `BOOT-002` “No boot device” / disco no detectado (orden de arranque, disco desconectado o muerto) — GUIADA/DERIVA
- `BOOT-003` INACCESSIBLE_BOOT_DEVICE tras cambiar modo SATA/RAID/VMD en la BIOS — GUIADA 🟡
- `BOOT-004` BCD dañado → `bcdboot` desde WinRE o rescate — AUTO 🟡
- `BOOT-005` Actualización pendiente que impide arrancar → revertir acciones pendientes — AUTO 🟡
- `BOOT-BL-001` **Pantalla de recuperación de BitLocker**: dónde encontrar la clave (cuenta Microsoft, impresa, organización) y por qué apareció — GUIADA 🟢
- `BOOT-BL-002` Antes de tocar la BIOS: verificar BitLocker y tener la clave — AUTO 🟢 (bloqueo preventivo)
- `BOOT-SB-001` Secure Boot desactivado / certificados 2023 no aplicados — GUIADA 🟡
- `BOOT-SB-002` El pendrive de rescate no arranca (Secure Boot, CA de terceros, modo Legacy, tecla de menú por fabricante) — GUIADA 🟢
- `BOOT-BIOS-001` Cómo entrar a la BIOS/UEFI según marca y modelo detectado — GUIADA 🟢
- `BOOT-BIOS-002` Actualización de BIOS: solo desde el fabricante, con batería cargada/cargador — DERIVA 🔴
- `BOOT-TPM-001` Habilitar TPM (fTPM/PTT) — GUIADA 🟡

## ★ O. Estafas y protección del usuario (30)

- `SCAM-001` Página que dice “Tu PC está infectada, llamá a este número” (bloqueo del navegador) → cerrar con seguridad, explicar que es una estafa — AUTO 🟢
- `SCAM-002` **Programa de acceso remoto instalado** (AnyDesk, TeamViewer, RustDesk, ScreenConnect, UltraViewer…) → “¿Lo instalaste vos? ¿Te llamó alguien?” → desinstalar y revisar — GUIADA 🟡
- `SCAM-003` Alguien llamó haciéndose pasar por Microsoft/el banco → pasos inmediatos (cambiar claves desde otro dispositivo, avisar al banco) — GUIADA 🟢
- `SCAM-004` Correo o mensaje sospechoso (phishing): cómo reconocerlo — GUIADA 🟢
- `SCAM-005` Antivirus falso / “optimizador” que pide pagar — AUTO 🟡
- `SCAM-006` Extensión de navegador que roba datos — AUTO 🟡
- `SCAM-007` ★ Compra de software “barato” que resultó ilegal o con malware — GUIADA 🟢

## ★ P. Notebooks, batería y energía (35)

- `LAP-BAT-001` Salud de la batería (capacidad de diseño vs actual, ciclos) — AUTO 🟢
- `LAP-BAT-002` La batería no carga / “conectado, no se está cargando” (límite de carga del fabricante, cargador, batería) — GUIADA/DERIVA
- `LAP-BAT-003` La batería dura poco: qué consume — AUTO 🟢
- `LAP-HOT-001` Se calienta mucho / ventilador al máximo (polvo, pasta térmica) — DERIVA + GUIADA
- `LAP-LID-001` ★ Qué hace al cerrar la tapa — AUTO 🟡
- `LAP-FN-001` ★ Teclas de función invertidas (Fn lock) / brillo no funciona — GUIADA 🟢
- `LAP-CHG-001` ★ Cargador USB-C de potencia insuficiente — GUIADA 🟢

## ★ Q. Teclado, idioma, accesibilidad y configuración básica (40)

- `BAS-KB-001` **“No me sale la @, la ñ o los acentos”** (distribución de teclado incorrecta: Latinoamericano vs Español vs Inglés) — AUTO 🟡
- `BAS-KB-002` El teclado escribe números en lugar de letras / Bloq Num — GUIADA 🟢
- `BAS-KB-003` Teclas especiales activadas sin querer (Teclas especiales, Teclas filtro) — AUTO 🟡
- `BAS-TXT-001` **Letras muy chicas** → escala, tamaño de texto, lupa — AUTO 🟡
- `BAS-ACC-001` Narrador o lupa que se activaron sin querer — AUTO 🟡
- `BAS-LANG-001` Windows en otro idioma → agregar idioma — GUIADA 🟡
- `BAS-NOTI-001` Demasiadas notificaciones — AUTO 🟡
- `BAS-WALL-001` ★ Cambiar fondo, ícono del escritorio que desapareció, restaurar íconos del escritorio — AUTO 🟢
- `BAS-CTX-001` ★ Menú contextual “clásico” de Windows 11 — AUTO 🟡

## ★ R. Celulares, TV y dispositivos conectados (30)

- `PHN-001` Pasar fotos del celular a la PC (USB/MTP: “solo carga”, Vínculo Móvil) — GUIADA 🟢
- `PHN-002` El celular no aparece al conectarlo — GUIADA 🟡
- `PHN-003` WhatsApp Desktop / Web no vincula — GUIADA 🟢
- `TV-001` Proyectar en el televisor (HDMI, Miracast con Win+K) — GUIADA 🟢
- `TV-002` Se ve en la TV pero el sonido sale por la PC — AUTO 🟡
- `TV-003` ★ Imagen cortada en los bordes de la TV (overscan) — GUIADA 🟢

## ★ S. Copias, migración y reinstalación (25)

- `MIG-001` **Copia de seguridad de emergencia** de Documentos, Escritorio, Fotos, Descargas a un disco externo (verificando cada archivo) — AUTO 🟡
- `MIG-002` Pasar mis cosas a una PC nueva (archivos, lista de programas, favoritos del navegador) — GUIADA 🟡
- `MIG-003` Pasar Windows de HDD a SSD (clonado) — GUIADA 🔴
- `MIG-004` **Asistente de reinstalación limpia**: copia de archivos, exportación de controladores para reinstalarlos después, lista de programas, ISO oficial descargada por el usuario — GUIADA 🔴
- `MIG-005` Restablecer este equipo (conservando archivos o no), explicado sin tecnicismos — GUIADA 🔴

## ★ T. Mejoras de hardware y ciclo de vida (20)

- `UPG-001` **“¿Qué le puedo mejorar a mi PC?”**: SSD si el disco del sistema es mecánico, RAM a 8 GB y doble canal, con especificaciones exactas para comprar (sin publicidad ni enlaces de afiliados) — AUTO 🟢
- `UPG-002` ¿Vale la pena reparar o conviene reemplazar? (antigüedad, compatibilidad con Windows 11, costo estimado de mejoras) — GUIADA 🟢
- `UPG-003` RAM funcionando por debajo de su velocidad nominal (perfil XMP/EXPO en BIOS) — GUIADA 🟡
- `UPG-004` ¿Qué fuente necesito para esta placa de video? (consumo informado de los componentes; no mide la fuente real) — GUIADA 🟢

## ★ U. Fallas físicas: diagnóstico y derivación (30)

- `HW-DSK-001` Disco con sectores reasignados/pendientes en aumento → **copia inmediata de datos**, no correr reparaciones intensivas, reemplazo — DERIVA 🟢
- `HW-RAM-001` Errores de memoria confirmados (mdsched o Memtest86+) → probar módulo por módulo — GUIADA/DERIVA
- `HW-PSU-001` Apagados bajo carga sin sobrecalentamiento ni errores de software → **posible** fuente; se explica que Windows no puede medirla y cómo confirmarlo — DERIVA
- `HW-FAN-001` Ventilador que no gira (0 RPM informado) — DERIVA
- `HW-GPU-001` Artefactos en pantalla también en la BIOS → falla de GPU o monitor, no de software — DERIVA
- `HW-SCR-001` Pantalla de notebook rota / líneas — DERIVA
- `HW-LIQ-001` Derrame de líquido: qué hacer ya (apagar, no cargar) — GUIADA/DERIVA
- `HW-CMOS-001` Pila del reloj agotada (hora que se pierde) — DERIVA

---

## Funciones transversales agregadas ★

Además de los problemas, estas funciones se agregaron porque resuelven situaciones reales que tu lista no cubría:

1. **Capturar el mensaje de error**: el usuario toca el botón y luego la ventana del error; PC Doctor lee el texto exacto (UI Automation, o reconocimiento de texto como respaldo) y lo busca en la biblioteca.
2. **Hablarle a PC Doctor** y que lea las instrucciones en voz alta, sin Internet.
3. **Bloqueo preventivo por BitLocker** antes de cualquier cambio en la BIOS o arranque desde USB.
4. **Detección de estafas y accesos remotos** instalados por terceros.
5. **Detector de pendrives y discos falsos.**
6. **Historial por PC** guardado en el pendrive: un técnico que atiende muchas PCs ve la evolución (por ejemplo, el SMART de un disco que empeora visita tras visita). Se puede borrar.
7. **Informe para el técnico o el soporte oficial**: HTML imprimible con la evidencia, para no explicar todo de nuevo.
8. **Copia de seguridad de emergencia** antes de cualquier reparación roja.
9. **Asesor de mejoras** y de “reparar o reemplazar”.
10. **Actualizar el pendrive desde cualquier otra PC** cuando la PC rota no tiene Internet.
11. **Paquete de controladores de red** para el caso “reinstalé Windows y no tengo Internet”.
12. **Modo técnico multi-PC** con la CLI para técnicos y escuelas.
