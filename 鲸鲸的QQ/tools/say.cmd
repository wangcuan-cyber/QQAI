@echo off
chcp 65001 >nul
rem say - speak text as a QQ voice message.
rem
rem Usage:
rem   say "hello"
rem   echo hello | say
rem   say --file lines.txt
rem   say "hello" --to group:123456
rem   say "hello" --pitch +4Hz --rate -10% --post sweet
rem
rem Depends only on SnowLuma (OneBot :3000). No model, no bridge, no DSH needed.
rem
rem NOTE: this file is ASCII-only on purpose. cmd.exe reads .cmd as the system
rem ANSI codepage (GBK here); UTF-8 Chinese in comments turns into mojibake and
rem breaks parsing ("'xist' is not recognized...").

setlocal
set "NODE=C:\Users\xt621\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe"
set "SAY=C:\WhaleQQ\tools\say.mjs"
if not exist "%NODE%" (
  echo [say] node not found at %NODE%
  exit /b 1
)
if not exist "%SAY%" (
  echo [say] say.mjs not found at %SAY%
  exit /b 1
)
"%NODE%" "%SAY%" %*
exit /b %errorlevel%
