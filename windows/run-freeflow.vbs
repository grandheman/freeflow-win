' Launches FreeFlow through the Microsoft-signed .NET host.
'
' Why this exists: with Smart App Control enabled, Windows blocks unsigned
' executables from loading, so FreeFlow.exe will not start unless it has been
' code-signed. Running the managed assembly through dotnet.exe works because the
' host itself is signed by Microsoft. This is the free way to run your own build
' on your own machine without disabling Smart App Control.
'
' VBScript rather than a .cmd file because WScript.Shell can start the process
' with a hidden window, so no console flashes on screen at launch.
'
' Usage: double-click, or point a shortcut at it.

Option Explicit

Dim shell, fso, scriptDir, assembly, dotnet, candidates, candidate

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)

' Prefer a Release build, fall back to Debug.
candidates = Array( _
    "src\FreeFlow.App\bin\Release\net8.0-windows10.0.19041.0\FreeFlow.dll", _
    "src\FreeFlow.App\bin\Debug\net8.0-windows10.0.19041.0\FreeFlow.dll")

assembly = ""
For Each candidate In candidates
    If assembly = "" Then
        If fso.FileExists(fso.BuildPath(scriptDir, candidate)) Then
            assembly = fso.BuildPath(scriptDir, candidate)
        End If
    End If
Next

If assembly = "" Then
    MsgBox "FreeFlow is not built yet." & vbCrLf & vbCrLf & _
           "Run this first:" & vbCrLf & _
           "  dotnet build FreeFlow.sln --configuration Release", _
           vbExclamation, "FreeFlow"
    WScript.Quit 1
End If

dotnet = fso.BuildPath(shell.ExpandEnvironmentStrings("%ProgramFiles%"), "dotnet\dotnet.exe")

If Not fso.FileExists(dotnet) Then
    MsgBox "The .NET 8 runtime was not found at:" & vbCrLf & dotnet & vbCrLf & vbCrLf & _
           "Install it from https://dotnet.microsoft.com/download/dotnet/8.0", _
           vbExclamation, "FreeFlow"
    WScript.Quit 1
End If

' 0 = hidden window, False = do not wait for exit.
shell.Run """" & dotnet & """ """ & assembly & """", 0, False
