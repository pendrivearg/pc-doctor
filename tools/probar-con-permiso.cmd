@echo off
rem Corre las pruebas de las reparaciones que necesitan permiso de administrador.
rem Hay que abrirlo con clic derecho > "Ejecutar como administrador" (o lo lanza PC Doctor en desarrollo).
rem Cambios reales que hace: reinicia los servicios de impresion y de audio (se cortan un segundo),
rem actualiza las firmas de Defender y hace un analisis rapido. Todo lo demas usa carpetas y claves de prueba.
cd /d "%~dp0.."
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
"%DOTNET_ROOT%\dotnet.exe" test tests\PCDoctor.Tests --filter "FullyQualifiedName~AdminActionTests" --logger "console;verbosity=normal" > "%~dp0resultado-pruebas-admin.txt" 2>&1
echo Listo. Resultado en tools\resultado-pruebas-admin.txt
