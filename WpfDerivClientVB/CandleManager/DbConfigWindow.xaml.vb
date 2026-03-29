Imports System.Windows

' ================================================================
'  DbConfigWindow
'  Ventana secundaria que hostea DbConfigControl.
'  Se abre con .ShowDialog() o .Show() según el contexto.
'  Misma lógica que AdminDashboard inyecta AdminSidebar.
' ================================================================
Partial Public Class DbConfigWindow
    Inherits Window

    Private _configControl As DbConfigControl

    ' ── Propiedad: la config guardada (útil si se abre modal) ────
    Public ReadOnly Property SavedSettings As WpfDerivClientVB.DatabaseSettings

    ' ── Constructor ──────────────────────────────────────────────
    Public Sub New()
        InitializeComponent()
        InyectarControl()
    End Sub

    Private Sub InyectarControl()
        _configControl = New DbConfigControl()
        ConfigContainer.Content = _configControl

        ' Escuchar el evento de guardado
        AddHandler _configControl.ConfigSaved, AddressOf OnConfigSaved
    End Sub

    ' ── Callback: configuración guardada ─────────────────────────
    Private Sub OnConfigSaved(settings As WpfDerivClientVB.DatabaseSettings)
        _SavedSettings = settings
        ' Si se abrió como modal (ShowDialog), cerramos al guardar
        If Me.IsLoaded AndAlso Me.DialogResult Is Nothing Then
            Try
                Me.DialogResult = True
            Catch
                ' Si no es modal, solo notificamos
            End Try
        End If
    End Sub

    ' ── Abre la ventana como modal (para uso desde CandleViewerWindow) ──
    ''' <summary>Factory: abre la ventana de configuración como diálogo modal.</summary>
    Public Shared Function AbrirModal(owner As Window) As WpfDerivClientVB.DatabaseSettings
        Dim win As New DbConfigWindow()
        win.Owner = owner
        win.ShowDialog()
        Return win.SavedSettings
    End Function

End Class
