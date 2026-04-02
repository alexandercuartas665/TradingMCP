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

        Private Async Sub CargarClientes()
            Try
                Await _repo.EnsureTablesExistAsync()
                Dim lista = Await _repo.GetAllAsync()
                lstClients.ItemsSource = lista
            Catch ex As Exception
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            End Try
        End Sub

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
                        txtRealAccountId.Text = creds.AccountIdReal
                        txtRealAppId.Text = creds.AppIdReal
                        txtRealToken.Text = creds.ApiTokenReal
                        txtVirAccountId.Text = creds.AccountIdVirtual
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

                _selectedClient.DerivCredentials.AccountIdReal = txtRealAccountId.Text.Trim()
                _selectedClient.DerivCredentials.AppIdReal = txtRealAppId.Text.Trim()
                _selectedClient.DerivCredentials.ApiTokenReal = txtRealToken.Text.Trim()
                _selectedClient.DerivCredentials.AccountIdVirtual = txtVirAccountId.Text.Trim()
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
            txtRealAccountId.Text = ""
            txtRealAppId.Text = ""
            txtRealToken.Text = ""
            txtVirAccountId.Text = ""
            txtVirAppId.Text = ""
            txtVirToken.Text = ""
            txtTestRealResult.Text = ""
            txtTestVirResult.Text = ""
        End Sub

        Private Sub DeshabilitarEdicion()
            gridDetails.IsEnabled = False
            gridDeriv.IsEnabled = False
            btnSave.IsEnabled = False
            txtName.Text = ""
            txtEmail.Text = ""
            LimpiarCamposDeriv()
        End Sub

        Private Async Sub btnTestRealToken_Click(sender As Object, e As RoutedEventArgs)
            Await TestTokenAsync(txtRealAccountId.Text.Trim(), txtRealAppId.Text.Trim(), txtRealToken.Text.Trim(), isDemo:=False, resultLabel:=txtTestRealResult)
        End Sub

        Private Async Sub btnTestVirToken_Click(sender As Object, e As RoutedEventArgs)
            Await TestTokenAsync(txtVirAccountId.Text.Trim(), txtVirAppId.Text.Trim(), txtVirToken.Text.Trim(), isDemo:=True, resultLabel:=txtTestVirResult)
        End Sub

        ''' <summary>
        ''' Nuevo flujo Deriv API:
        ''' 1. POST REST con Bearer token para obtener OTP de un solo uso
        ''' 2. Conectar WebSocket usando ese OTP
        ''' </summary>
        Private Async Function TestTokenAsync(accountId As String, appId As String, token As String, isDemo As Boolean, resultLabel As TextBlock) As Task
            If String.IsNullOrWhiteSpace(token) Then
                resultLabel.Text = "❌ Token vacío"
                resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                Return
            End If
            If String.IsNullOrWhiteSpace(accountId) Then
                resultLabel.Text = "❌ Account ID vacío"
                resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                System.Windows.MessageBox.Show(
                "Ingresa el Account ID." & vbCrLf & "Demo: DOT90355933 | Real: CR123456 (ver en tu perfil Deriv)",
                "Campo requerido", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                Return
            End If
            If String.IsNullOrWhiteSpace(appId) Then
                resultLabel.Text = "❌ App ID vacío"
                resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                System.Windows.MessageBox.Show(
                "Ingresa el OAuth App ID de tu aplicación Deriv." & vbCrLf & "Ej: 32SQld9iNe8Q4FJx0BEJP",
                "Campo requerido", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                Return
            End If

            resultLabel.Text = "⏳ Obteniendo OTP..."
            resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Yellow)

            Try
                ' Paso 1: Obtener OTP via REST (usando WebClient para evitar conflictos de ensamblado en .NET 4.8)
                Dim otp As String = Nothing
                Dim otpUrl = String.Format("https://api.derivws.com/trading/v1/options/accounts/{0}/otp", accountId)

                Dim responseBody As String = Nothing
                Dim httpStatusCode As Integer = 0

                Try
                    responseBody = Await Task.Run(Function()
                                                      Using wc As New System.Net.WebClient()
                                                          wc.Headers.Add("Authorization", "Bearer " & token)
                                                          wc.Headers.Add("deriv-app-id", appId)
                                                          Return wc.UploadString(otpUrl, "POST", "")
                                                      End Using
                                                  End Function)
                    httpStatusCode = 200
                Catch webEx As System.Net.WebException
                    If webEx.Response IsNot Nothing Then
                        Using errStream = webEx.Response.GetResponseStream()
                            Using reader As New System.IO.StreamReader(errStream)
                                responseBody = reader.ReadToEnd()
                            End Using
                        End Using
                        httpStatusCode = CInt(CType(webEx.Response, System.Net.HttpWebResponse).StatusCode)
                    End If
                    resultLabel.Text = "❌ Error HTTP " & httpStatusCode.ToString()
                    resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                    System.Windows.MessageBox.Show(
                    "Error al obtener OTP:" & vbCrLf & "HTTP " & httpStatusCode.ToString() & vbCrLf & vbCrLf & responseBody,
                    "Error OTP", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                    Return
                End Try

                ' La respuesta trae: {"data":{"url":"wss://api.derivws.com/...?otp=XXXXX"},"meta":{...}}
                Dim otpObj = Newtonsoft.Json.Linq.JObject.Parse(responseBody)
                Dim wsUrl As String = Nothing
                If otpObj("data") IsNot Nothing AndAlso otpObj("data")("url") IsNot Nothing Then
                    wsUrl = otpObj("data")("url").ToString()
                Else
                    resultLabel.Text = "❌ URL WebSocket no encontrada"
                    resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                    System.Windows.MessageBox.Show(
                    "Respuesta inesperada:" & vbCrLf & responseBody,
                    "Error OTP", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning)
                    Return
                End If

                resultLabel.Text = "⏳ Conectando WebSocket..."

                Using ws As New System.Net.WebSockets.ClientWebSocket()
                    Using cts As New System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10))
                        Await ws.ConnectAsync(New Uri(wsUrl), cts.Token)

                        resultLabel.Text = $"✅ Conectado ({If(isDemo, "Demo", "Real")})"
                        resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen)

                        Try
                            Await ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "OK", System.Threading.CancellationToken.None)
                        Catch
                        End Try
                    End Using
                End Using

            Catch ex As Exception
                resultLabel.Text = "❌ Error de conexión"
                resultLabel.Foreground = New System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red)
                System.Windows.MessageBox.Show($"Error:{vbCrLf}{ex.Message}", "Error Crítico", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error)
            End Try
        End Function
    End Class
End Namespace

