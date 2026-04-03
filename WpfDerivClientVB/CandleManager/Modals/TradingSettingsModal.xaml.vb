Imports System.Windows
Imports WpfDerivClientVB.WpfDerivClientVB

Public Partial Class TradingSettingsModal
    Inherits Window

    Public ReadOnly Property SelectedClientId As Integer?
        Get
            Dim client = TryCast(cmbCliente.SelectedItem, ClientModel)
            If client IsNot Nothing Then
                Return client.Id
            End If
            Return Nothing
        End Get
    End Property

    Public ReadOnly Property SelectedAccountType As String
        Get
            Dim itm = TryCast(cmbTipoCuenta.SelectedItem, Controls.ComboBoxItem)
            If itm IsNot Nothing Then
                Return itm.Content.ToString()
            End If
            Return "Demo"
        End Get
    End Property

    Public ReadOnly Property SelectedPlatform As String
        Get
            Dim itm = TryCast(cmbPlataforma.SelectedItem, Controls.ComboBoxItem)
            If itm IsNot Nothing Then
                Return itm.Content.ToString()
            End If
            Return "Deriv"
        End Get
    End Property

    Public Sub New()
        InitializeComponent()
    End Sub

    Protected Overrides Sub OnSourceInitialized(e As EventArgs)
        MyBase.OnSourceInitialized(e)
        ThemeHelper.ApplyDarkTitleBar(Me)
    End Sub

    Private Async Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles MyBase.Loaded
        Try
            Dim clientRepo As New ClientRepository()
            Dim clientes = Await clientRepo.GetAllAsync()
            cmbCliente.ItemsSource = clientes
            If clientes.Count > 0 Then
                cmbCliente.SelectedIndex = 0
            End If
        Catch ex As Exception
            MessageBox.Show("Error cargando clientes: " & ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try
    End Sub

    Private Sub BtnCancelar_Click(sender As Object, e As RoutedEventArgs)
        Me.DialogResult = False
        Me.Close()
    End Sub

    Private Sub BtnGuardar_Click(sender As Object, e As RoutedEventArgs)
        If cmbCliente.SelectedItem Is Nothing Then
            MessageBox.Show("Por favor, seleccione un cliente válido.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning)
            Return
        End If
        Me.DialogResult = True
        Me.Close()
    End Sub
End Class
