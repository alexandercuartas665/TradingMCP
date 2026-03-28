Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Threading

Partial Public Class AdminDashboard
    Inherits Window

    Private _clockTimer As DispatcherTimer

    Public Sub New()
        InitializeComponent()
        
        ' 1. Inyectar el Sidebar dinámicamente
        Dim sidebarControl As New AdminSidebar()
        SidebarContainer.Content = sidebarControl
        
        ' 2. Escuchar los eventos de navegación
        AddHandler sidebarControl.MenuItemSelected, AddressOf OnSidebarItemSelected
        
        IniciarReloj()
    End Sub

    ' ================================================================
    '  NAVEGACIÓN DEL SIDEBAR 
    ' ================================================================
    Private Sub OnSidebarItemSelected(menuName As String)
        Select Case menuName
            Case "Dashboard"
                ' Lógica para mostrar la vista Dashboard
            Case "Users"
                MessageBox.Show("Vista de Usuarios no implementada.", "Aviso",
                                MessageBoxButton.OK, MessageBoxImage.Information)
            Case "Accounts"
                MessageBox.Show("Vista de Cuentas no implementada.", "Aviso",
                                MessageBoxButton.OK, MessageBoxImage.Information)
            Case "Platforms"
                MessageBox.Show("Vista de Plataformas no implementada.", "Aviso",
                                MessageBoxButton.OK, MessageBoxImage.Information)
            Case "Monitoring"
                MessageBox.Show("Vista de Monitoreo no implementada.", "Aviso",
                                MessageBoxButton.OK, MessageBoxImage.Information)
            Case "Settings"
                MessageBox.Show("Vista de Configuración no implementada.", "Aviso",
                                MessageBoxButton.OK, MessageBoxImage.Information)
        End Select
    End Sub

    ' ================================================================
    '  RELOJ EN TIEMPO REAL
    ' ================================================================
    Private Sub IniciarReloj()
        _clockTimer = New DispatcherTimer()
        _clockTimer.Interval = TimeSpan.FromSeconds(1)
        AddHandler _clockTimer.Tick, AddressOf ActualizarReloj
        _clockTimer.Start()
        ActualizarReloj(Nothing, Nothing)
    End Sub

    Private Sub ActualizarReloj(sender As Object, e As EventArgs)
        Dim ahora As DateTime = DateTime.UtcNow
        Dim horaStr As String = ahora.ToString("HH:mm:ss") & " UTC"
        txtClock.Text = horaStr
        txtLastUpdated.Text = horaStr
    End Sub

    ' ================================================================
    '  CIERRE
    ' ================================================================
    Private Sub Window_Closing(sender As Object, e As System.ComponentModel.CancelEventArgs) Handles Me.Closing
        If _clockTimer IsNot Nothing Then _clockTimer.Stop()
    End Sub

End Class
