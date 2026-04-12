Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Windows
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB

' ================================================================
'  TradingSettingsModal
'  Cascada: Plataforma -> Cliente -> Tipo de Cuenta -> Activo
' ================================================================
Namespace WpfDerivClientVB

    Partial Public Class TradingSettingsModal
        Inherits Window

        ' Flag para ignorar eventos durante la inicializaci�n
        Private _cargando As Boolean = False

        ' ?? Propiedades p�blicas ??????????????????????????????????????
        Public ReadOnly Property SelectedClientId As Integer
            Get
                Dim client = TryCast(cmbCliente.SelectedItem, ClientModel)
                Return If(client IsNot Nothing, CType(client.Id, Integer?), Nothing)
            End Get
        End Property

        Public ReadOnly Property SelectedClient As ClientModel
            Get
                Return TryCast(cmbCliente.SelectedItem, ClientModel)
            End Get
        End Property

        Public ReadOnly Property SelectedAccountType As String
            Get
                Dim itm = TryCast(cmbTipoCuenta.SelectedItem, Controls.ComboBoxItem)
                Return If(itm?.Tag?.ToString(), "demo")
            End Get
        End Property

        Public ReadOnly Property SelectedPlatform As String
            Get
                Dim itm = TryCast(cmbPlataforma.SelectedItem, Controls.ComboBoxItem)
                Return If(itm?.Tag?.ToString(), "deriv")
            End Get
        End Property

        Public ReadOnly Property SelectedAssetSymbol As String
            Get
                Dim itm = TryCast(cmbActivo.SelectedItem, ActivoItem)
                Return If(itm?.Symbol, "R_100")
            End Get
        End Property

        Public ReadOnly Property SelectedAssetName As String
            Get
                Dim itm = TryCast(cmbActivo.SelectedItem, ActivoItem)
                Return If(itm?.Nombre, "Volatility 100 Index")
            End Get
        End Property

        ' ?? Constructor ???????????????????????????????????????????????
        Public Sub New()
            InitializeComponent()
        End Sub

        Protected Overrides Sub OnSourceInitialized(e As EventArgs)
            MyBase.OnSourceInitialized(e)
            ThemeHelper.ApplyDarkTitleBar(Me)
        End Sub

        ' ?? CARGA INICIAL ????????????????????????????????????????????
        Private Async Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles MyBase.Loaded
            _cargando = True
            cmbPlataforma.SelectedIndex = 0
            _cargando = False

            ' Cargar clientes y activos en paralelo
            Await Task.WhenAll(CargarClientesAsync(), CargarActivosAsync())
        End Sub

        Private Async Function CargarClientesAsync() As System.Threading.Tasks.Task
            txtLoadingClientes.Visibility = Visibility.Visible
            cmbCliente.IsEnabled = False
            cmbTipoCuenta.IsEnabled = False
            cmbActivo.IsEnabled = False
            btnGuardar.IsEnabled = False

            Try
                Dim repo As New ClientRepository()
                Dim lista = Await repo.GetAllAsync()
                cmbCliente.ItemsSource = lista

                If lista.Count > 0 Then
                    cmbCliente.SelectedIndex = 0   ' dispara CmbCliente_SelectionChanged
                End If
                cmbCliente.IsEnabled = (lista.Count > 0)

                If lista.Count = 0 Then
                    MessageBox.Show("No hay clientes registrados en el sistema.",
                                "Sin clientes", MessageBoxButton.OK, MessageBoxImage.Information)
                End If
            Catch ex As Exception
                MessageBox.Show("Error cargando clientes: " & ex.Message,
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error)
            Finally
                txtLoadingClientes.Visibility = Visibility.Collapsed
            End Try
        End Function

        ' ?? CASCADA PLATAFORMA ????????????????????????????????????????
        Private Sub CmbPlataforma_SelectionChanged(sender As Object, e As Controls.SelectionChangedEventArgs)
            If _cargando Then Return
            ' Si se cambia de plataforma, resetear todo hacia abajo
            ResetCliente()
            ResetTipoCuenta()
            ResetActivo()
        End Sub

        ' ?? CASCADA CLIENTE ???????????????????????????????????????????
        Private Async Sub CmbCliente_SelectionChanged(sender As Object, e As Controls.SelectionChangedEventArgs)
            If _cargando Then Return

            ' Resetear niveles inferiores
            ResetTipoCuenta()
            ResetActivo()

            Dim client = TryCast(cmbCliente.SelectedItem, ClientModel)
            If client Is Nothing Then Return

            ' Cargar credenciales para ver qu� tipos de cuenta tiene activos
            Try
                Dim repo As New ClientRepository()
                Dim creds = Await repo.GetDerivCredentialsAsync(client.Id)
                client.DerivCredentials = creds

                Dim tieneDemo = creds IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(creds.ApiTokenVirtual)
                Dim tieneReal = creds IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(creds.ApiTokenReal)

                ' Habilitar / deshabilitar items del combobox
                Dim itemDemo = TryCast(cmbTipoCuenta.Items(0), Controls.ComboBoxItem)
                Dim itemReal = TryCast(cmbTipoCuenta.Items(1), Controls.ComboBoxItem)
                If itemDemo IsNot Nothing Then itemDemo.IsEnabled = tieneDemo
                If itemReal IsNot Nothing Then itemReal.IsEnabled = tieneReal

                cmbTipoCuenta.IsEnabled = (tieneDemo OrElse tieneReal)

                ' Preseleccionar primer tipo disponible
                If tieneDemo Then
                    cmbTipoCuenta.SelectedIndex = 0
                ElseIf tieneReal Then
                    cmbTipoCuenta.SelectedIndex = 1
                End If

            Catch
                ' Sin credenciales: habilitar ambas opciones de todos modos
                cmbTipoCuenta.IsEnabled = True
                cmbTipoCuenta.SelectedIndex = 0
            End Try
        End Sub

        ' ?? CASCADA TIPO DE CUENTA ????????????????????????????????????
        Private Sub CmbTipoCuenta_SelectionChanged(sender As Object, e As Controls.SelectionChangedEventArgs)
            If _cargando Then Return

            Dim client = TryCast(cmbCliente.SelectedItem, ClientModel)
            If client Is Nothing Then Return

            MostrarCredsBadge(client, SelectedAccountType)
            cmbActivo.IsEnabled = True
            ActualizarBtnGuardar()
        End Sub

        ' ?? BADGE DE CREDENCIALES ????????????????????????????????????
        Private Sub MostrarCredsBadge(client As ClientModel, tipoCuenta As String)
            If client?.DerivCredentials Is Nothing Then
                pnlCredBadge.Visibility = Visibility.Collapsed
                Return
            End If

            Dim creds = client.DerivCredentials
            Dim token As String
            Dim accountId As String

            If tipoCuenta = "demo" Then
                token = If(String.IsNullOrWhiteSpace(creds.ApiTokenVirtual), "�", creds.ApiTokenVirtual)
                accountId = If(String.IsNullOrWhiteSpace(creds.AccountIdVirtual), "�", creds.AccountIdVirtual)
            Else
                token = If(String.IsNullOrWhiteSpace(creds.ApiTokenReal), "�", creds.ApiTokenReal)
                accountId = If(String.IsNullOrWhiteSpace(creds.AccountIdReal), "�", creds.AccountIdReal)
            End If

            ' Enmascarar token por seguridad
            Dim tokenDisplay As String
            If token.Length > 8 Then
                tokenDisplay = token.Substring(0, 4) & "����" & token.Substring(token.Length - 4)
            Else
                tokenDisplay = "�"
            End If

            txtCredToken.Text = "Token:    " & tokenDisplay
            txtCredAccount.Text = "Account:  " & accountId
            pnlCredBadge.Visibility = Visibility.Visible
        End Sub

        ' ?? RESETEO POR NIVEL ????????????????????????????????????????
        Private Sub ResetCliente()
            cmbCliente.SelectedIndex = -1
            cmbCliente.IsEnabled = False
        End Sub

        Private Sub ResetTipoCuenta()
            cmbTipoCuenta.SelectedIndex = -1
            cmbTipoCuenta.IsEnabled = False
            pnlCredBadge.Visibility = Visibility.Collapsed
        End Sub

        Private Sub ResetActivo()
            cmbActivo.IsEnabled = False
            btnGuardar.IsEnabled = False
        End Sub

        ' ?? VALIDACI�N BOT�N GUARDAR ??????????????????????????????????
        Private Sub ActualizarBtnGuardar()
            Dim ok = cmbPlataforma.SelectedIndex >= 0 AndAlso
                 cmbCliente.SelectedIndex >= 0 AndAlso
                 cmbTipoCuenta.SelectedIndex >= 0 AndAlso
                 cmbActivo.SelectedIndex >= 0
            btnGuardar.IsEnabled = ok
        End Sub

        Private Sub CmbActivo_SelectionChanged(sender As Object, e As Controls.SelectionChangedEventArgs) Handles cmbActivo.SelectionChanged
            If _cargando Then Return
            ActualizarBtnGuardar()
        End Sub

        ' ?? BOTONES ??????????????????????????????????????????????????
        Private Sub BtnCancelar_Click(sender As Object, e As RoutedEventArgs)
            Me.DialogResult = False
            Me.Close()
        End Sub

        Private Sub BtnGuardar_Click(sender As Object, e As RoutedEventArgs)
            If cmbCliente.SelectedItem Is Nothing Then
                MessageBox.Show("Por favor selecciona un cliente v�lido.",
                            "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning)
                Return
            End If
            If cmbActivo.SelectedItem Is Nothing Then
                MessageBox.Show("Por favor selecciona un activo.",
                            "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning)
                Return
            End If
            Me.DialogResult = True
            Me.Close()
        End Sub

        ' ================================================================
        '  CARGA DINAMICA DE ACTIVOS DESDE DERIV API
        ' ================================================================
        Private Async Function CargarActivosAsync() As System.Threading.Tasks.Task
            txtLoadingActivos.Visibility = Visibility.Visible
            txtActivosError.Visibility = Visibility.Collapsed

            Dim ws As New ClientWebSocket()
            Dim cts As New CancellationTokenSource(TimeSpan.FromSeconds(12))
            Try
                Await ws.ConnectAsync(New Uri("wss://ws.derivws.com/websockets/v3?app_id=1089"), cts.Token)

                Dim req As New JObject()
                req.Add("active_symbols", "brief")
                req.Add("product_type", "basic")
                Dim reqBytes = Encoding.UTF8.GetBytes(req.ToString())
                Await ws.SendAsync(New ArraySegment(Of Byte)(reqBytes), WebSocketMessageType.Text, True, cts.Token)

                Dim buf(65535) As Byte
                Dim sb As New StringBuilder()
                Dim recvTask As Task(Of Boolean) = Task.Run(Async Function() As Task(Of Boolean)
                                                                Dim wsr As WebSocketReceiveResult
                                                                Do
                                                                    wsr = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buf), cts.Token)
                                                                    sb.Append(Encoding.UTF8.GetString(buf, 0, wsr.Count))
                                                                Loop While Not wsr.EndOfMessage
                                                                Return True
                                                            End Function)

                Dim ganador = Await Task.WhenAny(recvTask, Task.Delay(10000))
                cts.Cancel()

                If ganador IsNot recvTask Then
                    Throw New TimeoutException("Timeout al consultar simbolos de Deriv.")
                End If

                Dim resp = JObject.Parse(sb.ToString())
                Dim symbols = TryCast(resp("active_symbols"), JArray)
                If symbols Is Nothing Then
                    Throw New Exception(resp("error")?("message")?.ToString() & " (active_symbols)")
                End If

                Dim lista As New List(Of ActivoItem)()
                For Each sym As JToken In symbols
                    If sym("market")?.ToString() = "synthetic_index" AndAlso
                       sym("is_trading_suspended")?.ToObject(Of Integer)() = 0 Then
                        lista.Add(New ActivoItem(
                            sym("symbol").ToString(),
                            sym("display_name").ToString(),
                            If(sym("submarket_display_name")?.ToString(), "")
                        ))
                    End If
                Next

                ' Ordenar: primero por submarket, luego por nombre
                lista.Sort(Function(a, b)
                               Dim cmp = String.Compare(a.Submarket, b.Submarket, StringComparison.OrdinalIgnoreCase)
                               If cmp <> 0 Then Return cmp
                               Return String.Compare(a.Nombre, b.Nombre, StringComparison.OrdinalIgnoreCase)
                           End Function)

                cmbActivo.ItemsSource = lista

                ' Seleccionar R_100 por defecto
                Dim def = lista.FirstOrDefault(Function(x) x.Symbol = "R_100")
                If def IsNot Nothing Then
                    cmbActivo.SelectedItem = def
                ElseIf lista.Count > 0 Then
                    cmbActivo.SelectedIndex = 0
                End If

            Catch ex As Exception
                txtActivosError.Text = "No se pudieron cargar los activos: " & ex.Message
                txtActivosError.Visibility = Visibility.Visible
            Finally
                cts.Cancel()
                Try : ws.Dispose() : Catch : End Try
                txtLoadingActivos.Visibility = Visibility.Collapsed
            End Try
        End Function

    End Class

    ' ================================================================
    '  Modelo para item del combo de activos
    ' ================================================================
    Public Class ActivoItem
        Public Property Symbol    As String
        Public Property Nombre    As String
        Public Property Submarket As String

        Public ReadOnly Property Display As String
            Get
                Return Nombre & " / " & Symbol
            End Get
        End Property

        Public Sub New(symbol As String, nombre As String, submarket As String)
            Me.Symbol    = symbol
            Me.Nombre    = nombre
            Me.Submarket = If(submarket, "")
        End Sub

        Public Overrides Function ToString() As String
            Return Display
        End Function
    End Class

End Namespace
