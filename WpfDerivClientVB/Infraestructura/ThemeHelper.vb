Imports System.Runtime.InteropServices
Imports System.Windows.Interop
Imports System.Windows

Public Module ThemeHelper
    <DllImport("dwmapi.dll", PreserveSig:=True)>
    Private Function DwmSetWindowAttribute(hwnd As IntPtr, attr As Integer, ByRef attrValue As Integer, attrSize As Integer) As Integer
    End Function

    Private Const DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 As Integer = 19
    Private Const DWMWA_USE_IMMERSIVE_DARK_MODE As Integer = 20

    Public Sub ApplyDarkTitleBar(win As Window)
        Try
            Dim hwnd As IntPtr = New WindowInteropHelper(win).EnsureHandle()
            If hwnd <> IntPtr.Zero Then
                Dim isDark As Integer = 1
                ' Aplicar para Windows 11 y Windows 10 (20H1+)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, isDark, 4)
                ' Fallback para Windows 10 (antes de 20H1)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, isDark, 4)
            End If
        Catch ex As Exception
            ' Ignorar si hay un sistema operativo no compatible
        End Try
    End Sub
End Module
