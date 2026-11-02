# Política de privacidad de PC Doctor AI

PC Doctor AI revisa y repara la computadora **sin enviar datos a ningún lado**.

## Qué datos lee

Para diagnosticar, PC Doctor lee información técnica de la computadora: versión de Windows, estado del disco, de la memoria y de la batería, antivirus, red, programas que arrancan con Windows y el historial de errores de Windows. **No lee ni guarda contraseñas**, ni el contenido de tus documentos, fotos o correos.

En el **modo rescate**, si vos lo pedís, copia tus fotos y documentos al propio pendrive; y revisa archivos en busca de virus. Esa revisión ocurre dentro de la computadora: nada se envía.

## Dónde quedan

El historial de reparaciones y los informes quedan **solo en el pendrive** (carpeta `pcdoctor\state`), y lo rescatado en la carpeta `RESCATE`. Se pueden borrar cuando quieras.

Si pedís que PC Doctor **se abra solo al enchufar el pendrive**, en esa PC queda un programa chiquito (`PCDoctor-Abrir.exe`, 32 KB, en tu carpeta de usuario `AppData\Local\PCDoctor`) y una línea para que arranque con Windows. No lee nada ni se conecta a Internet: solo espera el aviso de Windows de que se enchufó una unidad y abre PC Doctor si el programa tiene el sello firmado del proyecto. Anota en un registro chiquito qué unidad abrió o rechazó. Se quita desde «Acerca de» > «Que no se abra solo en esta PC», y la PC queda como estaba.

## Cuándo se conecta a Internet

PC Doctor funciona sin Internet. Solo se conecta en dos casos, y en ninguno envía datos tuyos:

1. **«Buscar la versión nueva por Internet»** (en «Actualizar el pendrive»), únicamente cuando tocás ese botón: consulta en GitHub (`api.github.com` y `github.com`) si hay una versión nueva y la descarga. GitHub recibe lo mismo que recibe al bajar cualquier archivo desde un navegador (por ejemplo, la dirección IP). Ver la [política de privacidad de GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).
2. **Modo rescate con cable de red conectado**: actualiza las firmas de virus de ClamAV desde sus servidores oficiales (`database.clamav.net`). Si no hay cable de red, usa las firmas que trae el pendrive. Para evitarlo, basta con no conectar el cable.

Las páginas oficiales que ofrecen los pasos guiados (Microsoft, Google, Apple, WhatsApp) se abren en tu navegador solo si tocás su botón.

## Sin publicidad ni rastreo

PC Doctor no tiene publicidad, no tiene cuentas de usuario, no usa cookies ni estadísticas de uso, y nunca te va a llamar por teléfono ni pedir acceso remoto o pagos.

## Contacto

Para consultas sobre privacidad, abrí un «issue» en https://github.com/pendrivearg/pc-doctor/issues
