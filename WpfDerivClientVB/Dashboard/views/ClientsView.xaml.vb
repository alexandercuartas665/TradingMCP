Imports System.Windows
Imports System.Windows.Controls
Imports System.Threading.Tasks
Imports System.Collections.Generic

Namespace WpfDerivClientVB
    Partial Public Class ClientsView
        Inherits UserControl

        Private ReadOnly _repo As New ClientRepository()
        Private _selectedClient As ClientModel

        Public Sub New()
            InitializeComponent()
            CargarClientes()
        End Sub

        ''' <summary>
        ''' Carga la lista de clientes desde la base de datos.
        ''' Crea las tablas automáticamente si no existen (primera ejecución).
        ''' </summary>
        Private Async Sub CargarClientes()
            Try
                Await _repo.EnsureTablesExistAsync()
                Dim lista = Await _repo.GetAllAsync()
                lstClients.ItemsSource = lista
            Catch ex As Exception
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

        ''' <summary>
        ''' Evento al seleccionar un cliente de la lista.
        ''' </summary>
        Private Async Sub lstClients_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            _selectedClient = TryCast(lstClients.SelectedItem, ClientModel)

            If _selectedClient IsNot Nothing Then
                gridDetails.IsEnabled = True
                gridDeriv.IsEnabled = True
                btnSave.IsEnabled = True

                txtName.Text = _selectedClient.Name
                txtEmail.Text = _selectedClient.Email

                Try
                    Dim creds = Await _repo.GetDerivCredentialsAsync(_selectedClient.Id)
                    If creds IsNot Nothing Then
                        _selectedClient.DerivCredentials = creds
                        txtRealAppId.Text = creds.AppIdReal
                        txtRealToken.Text = creds.ApiTokenReal
                        txtVirAppId.Text = creds.AppIdVirtual
                        txtVirToken.Text = creds.ApiTokenVirtual
                    Else
                        LimpiarCamposDeriv()
                    End If
                Catch ex As Exception
                    MessageBox.Show("Error al cargar credenciales: " & ex.Message)
                End Try
            Else
                DeshabilitarEdicion()
            End If
        End Sub

        ''' <summary>
        ''' Prepara la UI para crear un nuevo cliente.
        ''' </summary>
        Private Sub btnAddClient_Click(sender As Object, e As RoutedEventArgs)
            lstClients.SelectedItem = Nothing
            _selectedClient = New ClientModel()

            gridDetails.IsEnabled = True
            gridDeriv.IsEnabled = True
            btnSave.IsEnabled = True

            txtName.Text = ""
            txtEmail.Text = ""
            LimpiarCamposDeriv()
            txtName.Focus()
        End Sub

        ''' <summary>
        ''' Guarda los cambios del cliente actual en la BD.
        ''' </summary>
        Private Async Sub btnSave_Click(sender As Object, e As RoutedEventArgs)
            If String.IsNullOrWhiteSpace(txtName.Text) Then
                MessageBox.Show("El nombre es obligatorio.")
                Return
            End If

            btnSave.IsEnabled = False
            btnSave.Content = "Guardando..."

            Try
                _selectedClient.Name = txtName.Text.Trim()
                _selectedClient.Email = txtEmail.Text.Trim()

                If _selectedClient.DerivCredentials Is Nothing Then
                    _selectedClient.DerivCredentials = New ClientDerivCredentials()
                End If

                _selectedClient.DerivCredentials.AppIdReal = txtRealAppId.Text.Trim()
                _selectedClient.DerivCredentials.ApiTokenReal = txtRealToken.Text.Trim()
                _selectedClient.DerivCredentials.AppIdVirtual = txtVirAppId.Text.Trim()
                _selectedClient.DerivCredentials.ApiTokenVirtual = txtVirToken.Text.Trim()

                Await _repo.SaveClientAsync(_selectedClient)

                MessageBox.Show("Cliente guardado correctamente.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information)
                CargarClientes()
            Catch ex As Exception
                MessageBox.Show("Error al guardar: " & ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            Finally
                btnSave.IsEnabled = True
                btnSave.Content = "Guardar Cliente"
            End Try
        End Sub

        Private Sub LimpiarCamposDeriv()
            txtRealAppId.Text = ""
            txtRealToken.Text = ""
            txtVirAppId.Text = ""
            txtVirToken.Text = ""
        End Sub

        Private Sub DeshabilitarEdicion()
            gridDetails.IsEnabled = False
            gridDeriv.IsEnabled = False
            btnSave.IsEnabled = False
            txtName.Text = ""
            txtEmail.Text = ""
            LimpiarCamposDeriv()
        End Sub
    End Class
End Namespace
