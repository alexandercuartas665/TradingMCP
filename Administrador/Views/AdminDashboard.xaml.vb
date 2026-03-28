Imports System.Windows
Imports System.Windows.Threading

Namespace Administrador

    ''' <summary>
    ''' Dashboard principal del sistema de administración.
    ''' Muestra overview del sistema: métricas, agentes activos, logs y sentiment.
    ''' </summary>
    Partial Public Class AdminDashboard
        Inherits Window

        Private _clockTimer As DispatcherTimer

        Public Sub New()
            InitializeComponent()
            IniciarReloj()
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
        '  NAVEGACIÓN SIDEBAR
        ' ================================================================
        Private Sub NavBtn_Click(sender As Object, e As RoutedEventArgs)
            ' Placehodler — future: mostrar panel correspondiente
            Dim btn = TryCast(sender, System.Windows.Controls.Button)
            If btn IsNot Nothing Then
                ' TODO: implementar navegación entre paneles
            End If
        End Sub

        ' ================================================================
        '  CIERRE
        ' ================================================================
        Private Sub Window_Closing(sender As Object, e As System.ComponentModel.CancelEventArgs) Handles Me.Closing
            If _clockTimer IsNot Nothing Then _clockTimer.Stop()
        End Sub

    End Class

End Namespace
