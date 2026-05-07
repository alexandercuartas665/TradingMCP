' ============================================================
' MainWindow.Trading.vb
' Contenido: Tab "Trading Live" completo:
'   - Conexión OTP al WebSocket de Deriv (nueva API)
'   - Botones simples CALL / PUT
'   - Panel avanzado: todos los tipos de contrato,
'     cotización previa + ejecución confirmada
'   - Bucle de escucha y procesamiento de mensajes trading
'     (balance, proposal, buy, proposal_open_contract, sell)
'   - Cierre forzado de contratos
'   - Guardado de operaciones en BD
' ============================================================
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        ' ===================================================
        '  TAB TRADING LIVE - Carga de clientes
        ' ===================================================
        Private Async Sub CargarClientesTrading()
            Try
                Dim repo As New ClientRepository()
                Dim lista = Await repo.GetAllAsync()
                cmbTradingClientes.ItemsSource = lista
                If lista.Count > 0 Then cmbTradingClientes.SelectedIndex = 0
            Catch ex As Exception
                txtTradingEstado.Text = "Error BD"
            End Try
        End Sub

        Private Async Sub cmbTradingClientes_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            _tradingClienteActual = TryCast(cmbTradingClientes.SelectedItem, ClientModel)
            If _tradingClienteActual Is Nothing Then Return
            Try
                Dim repo As New ClientRepository()
                _tradingCredsActual = Await repo.GetDerivCredentialsAsync(_tradingClienteActual.Id)
            Catch
            End Try
        End Sub

        ' ===================================================
        '  CONEXION / DESCONEXION TRADING
        ' ===================================================
        Private Async Sub btnConectarTrading_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingClienteActual Is Nothing Then
                System.Windows.MessageBox.Show("Selecciona un cliente primero.")
                Return
            End If
            If _tradingCredsActual Is Nothing Then
                System.Windows.MessageBox.Show("El cliente no tiene credenciales Deriv configuradas.")
                Return
            End If

            _tradingEsDemo = (cmbTradingTipoCuenta.SelectedIndex = 0)

            ' Cancelar cualquier sesión anterior antes de iniciar
            If _tradingCts IsNot Nothing Then _tradingCts.Cancel()
            _tradingCts = New CancellationTokenSource()

            btnConectarTrading.IsEnabled = False
            txtTradingEstado.Text = "Conectando..."
            txtTradingEstado.Foreground = New SolidColorBrush(Colors.Yellow)
            txtTradingBalance.Text = "--"

            Dim conectado As Boolean = Await ConectarTradingWsAsync(_tradingEsDemo)

            If conectado Then
                ' Cargar historial solo la primera vez (no en reconexiones)
                If gridOperaciones.ItemsSource Is Nothing Then
                    ' NO cargamos historial de trading_operations (manual repo) — duplicaba con
                    ' backtest_operaciones y traia ops eliminadas. El grid se llena solo con:
                    '   - Ops live de estrategias (via RecuperarOperacionesLivePendientesAsync)
                    '   - Trades manuales que el usuario haga durante la sesion actual
                    _tradingOperaciones.Clear()
                    gridOperaciones.ItemsSource = _tradingOperaciones
                End If

#Disable Warning BC42358
                Task.Run(Function() EscucharTradingAsync())
                Task.Run(Function() PingLoopAsync())
#Enable Warning BC42358

                ' ── Recuperar operaciones huerfanas del motor live ───────────────
                ' Cualquier operacion live que quedo "Pendiente"/"Abierto" en BD por un
                ' crash o cierre se reconcilia: la mostramos en el grid + nos suscribimos
                ' al contrato en Deriv para recibir su estado final.
                Await RecuperarOperacionesLivePendientesAsync()
                ' Refrescar PnL HOY/SEMANA/MES del panel lateral
                Await PanelLiveRefrescarPnlPeriodosAsync()
            Else
                btnConectarTrading.IsEnabled = True
            End If
        End Sub

        ''' <summary>
        ''' Llama al endpoint OTP de Deriv con reintentos. Maneja caidas transitorias
        ''' (host remoto cierra la conexion, timeout, 5xx) sin propagar la excepcion.
        ''' Devuelve el body JSON o "" si fallaron los 3 intentos.
        ''' </summary>
        Private Async Function ObtenerOtpConRetryAsync(otpUrl As String, token As String, appId As String, esDemo As Boolean) As Task(Of String)
            Dim demoras() As Integer = {0, 2000, 5000}   ' 0s, 2s, 5s
            For intento As Integer = 0 To demoras.Length - 1
                If demoras(intento) > 0 Then
                    Try
                        Await Task.Delay(demoras(intento), _tradingCts.Token)
                    Catch
                        Return ""
                    End Try
                End If
                Try
                    Using handler As New System.Net.Http.HttpClientHandler()
                        Using client As New System.Net.Http.HttpClient(handler)
                            client.Timeout = TimeSpan.FromSeconds(15)
                            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " & token)
                            client.DefaultRequestHeaders.TryAddWithoutValidation("deriv-app-id", appId)
                            Using content As New System.Net.Http.StringContent("", System.Text.Encoding.UTF8, "application/json")
                                Using resp = Await client.PostAsync(otpUrl, content, _tradingCts.Token)
                                    Dim body = Await resp.Content.ReadAsStringAsync()
                                    If resp.IsSuccessStatusCode Then
                                        If intento > 0 Then
                                            LogConexion(String.Format("OTP recuperado en intento #{0}.", intento + 1))
                                        End If
                                        Return body
                                    End If
                                    LogConexion(String.Format("OTP intento #{0} fallo: HTTP {1}", intento + 1, CInt(resp.StatusCode)))
                                End Using
                            End Using
                        End Using
                    End Using
                Catch ex As OperationCanceledException
                    Return ""
                Catch ex As Exception
                    LogConexion(String.Format("OTP intento #{0} error: {1}", intento + 1, ex.Message))
                End Try
            Next
            Return ""
        End Function

        ''' <summary>Obtiene OTP y conecta el WebSocket de trading. Devuelve True si tuvo éxito.</summary>
        Private Async Function ConectarTradingWsAsync(esDemo As Boolean) As Task(Of Boolean)
            Try
                Dim accountId As String = If(esDemo, _tradingCredsActual.AccountIdVirtual, _tradingCredsActual.AccountIdReal)
                Dim appId As String = If(esDemo, _tradingCredsActual.AppIdVirtual, _tradingCredsActual.AppIdReal)
                Dim token As String = If(esDemo, _tradingCredsActual.ApiTokenVirtual, _tradingCredsActual.ApiTokenReal)

                If String.IsNullOrWhiteSpace(accountId) OrElse String.IsNullOrWhiteSpace(token) Then
                    Dispatcher.Invoke(Sub() System.Windows.MessageBox.Show("Faltan Account ID o Token para la cuenta " & If(esDemo, "Demo", "Real") & "."))
                    Return False
                End If

                ' OTP via REST con retry para tolerar caidas transitorias del servidor de Deriv.
                ' Hasta 3 intentos con backoff: 0s, 2s, 5s.
                Dim otpUrl = String.Format("https://api.derivws.com/trading/v1/options/accounts/{0}/otp", accountId)
                Dim responseBody As String = Await ObtenerOtpConRetryAsync(otpUrl, token, appId, esDemo)
                If String.IsNullOrEmpty(responseBody) Then
                    Throw New Exception("No se pudo obtener OTP de Deriv tras 3 intentos.")
                End If

                Dim otpObj = JObject.Parse(responseBody)
                If otpObj("data") Is Nothing OrElse otpObj("data")("url") Is Nothing Then
                    Throw New Exception("Respuesta OTP inesperada: " & responseBody)
                End If
                Dim wsUrl As String = otpObj("data")("url").ToString()

                ' Conectar WebSocket con keepalive a nivel de protocolo (15s)
                ' Esto envía frames PING del estándar WebSocket — más efectivo que pings a nivel app
                _tradingWs = New ClientWebSocket()
                _tradingWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(15)
                Await _tradingWs.ConnectAsync(New Uri(wsUrl), _tradingCts.Token)

                Dispatcher.Invoke(Sub()
                                      txtTradingEstado.Text = If(esDemo, "Demo", "Real") & " - Conectado"
                                      txtTradingEstado.Foreground = New SolidColorBrush(Colors.LightGreen)
                                      btnConectarTrading.IsEnabled = False
                                      btnDesconectarTrading.IsEnabled = True
                                      btnTradingCall.IsEnabled = True
                                      btnTradingPut.IsEnabled = True
                                      btnCotizar.IsEnabled = True
                                      btnAnalizar.IsEnabled = True
                                      btnAnalizarYOperar.IsEnabled = True
                                      ' Reflejar estado en el panel lateral siempre visible
                                      PanelLiveSetConexion(True)
                                  End Sub)

                Await SuscribirBalanceTradingAsync()
                LogConexion(String.Format("✅ WebSocket trading conectado ({0}).", If(esDemo, "Demo", "Real")))
                Return True

            Catch ex As System.Net.WebException
                Dim errBody As String = ""
                If ex.Response IsNot Nothing Then
                    Using sr As New System.IO.StreamReader(ex.Response.GetResponseStream())
                        errBody = sr.ReadToEnd()
                    End Using
                End If
                Dispatcher.Invoke(Sub()
                                      txtTradingEstado.Text = "Error conexión"
                                      txtTradingEstado.Foreground = New SolidColorBrush(Colors.Red)
                                  End Sub)
                If Not _tradingCts.IsCancellationRequested Then
                    Dispatcher.Invoke(Sub() System.Windows.MessageBox.Show("Error al conectar:" & vbCrLf & errBody, "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error))
                End If
                Return False
            Catch ex As Exception
                Dispatcher.Invoke(Sub()
                                      txtTradingEstado.Text = "Error"
                                      txtTradingEstado.Foreground = New SolidColorBrush(Colors.Red)
                                  End Sub)
                If Not _tradingCts.IsCancellationRequested Then
                    Dispatcher.Invoke(Sub() System.Windows.MessageBox.Show("Error: " & ex.Message, "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error))
                End If
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Re-suscribe en Deriv todas las operaciones "Abierto" del grid para obtener su
        ''' estado real (won/lost/abierto). Util para limpiar fantasmas que ya expiraron en
        ''' Deriv pero el local nunca se entero.
        ''' </summary>
        Private Sub btnSincronizarOps_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("Conecta la cuenta de trading primero.", "Sincronizar")
                Return
            End If
            Dim abiertas = _tradingOperaciones.Where(
                Function(o) Not String.IsNullOrEmpty(o.ContractId) AndAlso
                            (o.Estado = "Abierto" OrElse o.Estado = "Pendiente")).ToList()
            If abiertas.Count = 0 Then
                txtTradingOpEstado.Text = "No hay operaciones Abierto/Pendiente para sincronizar."
                Return
            End If
            For Each op In abiertas
                SuscribirContratoAsync(op.ContractId)
            Next
            txtTradingOpEstado.Text = String.Format("Sincronizando {0} operacion(es) con Deriv...", abiertas.Count)
            LogConexion(String.Format("Re-suscripcion enviada para {0} contrato(s).", abiertas.Count))
        End Sub

        ''' <summary>
        ''' Vende (cierra) todas las operaciones que esten "Abierto" en el grid.
        ''' Pide confirmacion antes de hacerlo.
        ''' </summary>
        Private Async Sub btnCerrarTodasAbiertas_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("Conecta la cuenta de trading primero.", "Cerrar todas")
                Return
            End If
            Dim abiertas = _tradingOperaciones.Where(
                Function(o) Not String.IsNullOrEmpty(o.ContractId) AndAlso o.Estado = "Abierto").ToList()
            If abiertas.Count = 0 Then
                txtTradingOpEstado.Text = "No hay operaciones Abierto para cerrar."
                Return
            End If
            Dim confirm = System.Windows.MessageBox.Show(
                String.Format("Vender {0} operacion(es) Abierto?" & Environment.NewLine & Environment.NewLine &
                              "Cada una se vendera al precio de mercado actual de Deriv.",
                              abiertas.Count),
                "Confirmar cierre masivo",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning)
            If confirm <> System.Windows.MessageBoxResult.Yes Then Return

            txtTradingOpEstado.Text = String.Format("Cerrando {0} operaciones...", abiertas.Count)
            Dim cerradas As Integer = 0
            For Each op In abiertas
                Try
                    Dim req As New JObject()
                    req.Add("sell", op.ContractId)
                    req.Add("price", 0)   ' 0 = precio de mercado actual
                    Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                    Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes),
                                               WebSocketMessageType.Text, True, _tradingCts.Token)
                    cerradas += 1
                    Await Task.Delay(150)   ' espacio entre comandos para no saturar el WS
                Catch ex As Exception
                    LogConexion(String.Format("Error vendiendo {0}: {1}", op.ContractId, ex.Message))
                End Try
            Next
            txtTradingOpEstado.Text = String.Format("Comando 'sell' enviado a {0} de {1} operaciones.", cerradas, abiertas.Count)
        End Sub

        ''' <summary>
        ''' Tras conectar el Trading WS, carga las ultimas operaciones live (cualquier estado:
        ''' Ganado/Perdido/Pendiente/Abierto/Expirado) en el grid de Trading Live para mantener
        ''' visible el historial de la estrategia, y se suscribe a Deriv solo para las que
        ''' aun esten Pendiente/Abierto (las resueltas no necesitan refresh).
        ''' </summary>
        Private Async Function RecuperarOperacionesLivePendientesAsync() As Task
            Try
                Dim repo As New LiveTestRepository()
                Dim ops = Await repo.GetUltimasOperacionesLiveAsync(100)
                If ops.Count = 0 Then
                    LogConexion("Sin operaciones live previas por cargar.")
                    Return
                End If

                Dim suscritas As Integer = 0
                For Each p In ops
                    ' Crear OperacionModel para mostrar en el grid
                    Dim hora As String = ""
                    Try
                        hora = New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) _
                            .AddSeconds(p.EpochEntrada).ToLocalTime().ToString("HH:mm:ss")
                    Catch
                    End Try
                    Dim creadoEn As DateTime = DateTime.UtcNow
                    Try
                        creadoEn = New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(p.EpochEntrada)
                    Catch
                    End Try
                    Dim tiempoRest As String = If(p.Estado = "Pendiente" OrElse p.Estado = "Abierto", "?", "—")
                    Dim op As New OperacionModel With {
                        .ContractId      = p.ContractId,
                        .Hora            = hora,
                        .CreatedAt       = creadoEn,
                        .Tipo            = p.Tipo,
                        .Simbolo         = p.Symbol,
                        .Monto           = p.Monto,
                        .Estado          = p.Estado,
                        .ProfitLoss      = p.ProfitLoss,
                        .TiempoRestante  = tiempoRest,
                        .Estrategia      = p.Estrategia,
                        .EsLive          = True,
                        .SesionId        = p.SesionId,
                        .OperacionLiveId = p.OperacionId
                    }

                    ' Evitar duplicados (la op manual ya cargada con el mismo contract_id)
                    Dim yaExiste = _tradingOperaciones.Any(Function(o) o.ContractId = p.ContractId AndAlso Not String.IsNullOrEmpty(p.ContractId))
                    If Not yaExiste Then
                        _tradingOperaciones.Add(op)
                    End If

                    ' Solo suscribir las que aun no se resolvieron
                    If (p.Estado = "Pendiente" OrElse p.Estado = "Abierto") AndAlso
                       Not String.IsNullOrEmpty(p.ContractId) Then
                        SuscribirContratoAsync(p.ContractId)
                        suscritas += 1
                    End If
                Next
                LogConexion(String.Format("Cargadas {0} ops live (de las cuales {1} aun pendientes/abiertas).",
                                          ops.Count, suscritas))
            Catch ex As Exception
                LogConexion("Error cargando historial live: " & ex.Message)
            End Try
        End Function

        Private Sub btnDesconectarTrading_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingCts IsNot Nothing Then _tradingCts.Cancel()
            txtTradingEstado.Text = "Desconectado"
            txtTradingEstado.Foreground = New SolidColorBrush(Colors.OrangeRed)
            txtTradingBalance.Text = "--"
            ' Reflejar desconexion en el panel lateral siempre visible
            PanelLiveSetConexion(False)
            PanelLiveLimpiarSaldo()
            btnConectarTrading.IsEnabled = True
            btnDesconectarTrading.IsEnabled = False
            btnTradingCall.IsEnabled = False
            btnTradingPut.IsEnabled = False
            btnCotizar.IsEnabled = False
            btnEjecutarAvanzado.IsEnabled = False
            btnAnalizar.IsEnabled = False
            btnAnalizarYOperar.IsEnabled = False
        End Sub

        Private Async Function SuscribirBalanceTradingAsync() As Task
            Try
                Dim req As New JObject()
                req.Add("balance", 1)
                req.Add("subscribe", 1)
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
            Catch
            End Try
        End Function

        ' ===================================================
        '  BOTONES SIMPLES CALL / PUT (Tab Trading Live)
        ' ===================================================
        Private Async Sub btnTradingCall_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await EjecutarContratoTradingAsync("CALL")
        End Sub

        Private Async Sub btnTradingPut_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await EjecutarContratoTradingAsync("PUT")
        End Sub

        ' Botones CALL/PUT del tab Streaming (legacy, usan mismo WebSocket trading)
        Private Async Sub btnBuyCall_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await SendTradeRequest("CALL")
        End Sub

        Private Async Sub btnSellPut_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await SendTradeRequest("PUT")
        End Sub

        Private Async Function SendTradeRequest(contractType As String) As Task
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                txtTradeResult.Text = "⚡ Conecta una cuenta en el tab 'Trading Live' para operar."
                txtTradeResult.Foreground = New SolidColorBrush(Color.FromRgb(&H89, &HB4, &HFA))
                Return
            End If

            Dim symbol As String = txtTradeSymbol.Text.Trim()
            Dim amount As Double
            Dim duration As Integer

            If Not Double.TryParse(txtTradeAmount.Text.Trim(), amount) OrElse amount <= 0 Then
                txtTradeResult.Text = "❌ Monto inválido."
                txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                Return
            End If
            If Not Integer.TryParse(txtTradeDuration.Text.Trim(), duration) OrElse duration <= 0 Then
                txtTradeResult.Text = "❌ Duración inválida."
                txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                Return
            End If

            txtTradeResult.Text = "⏳ Solicitando cotización " & contractType & " " & symbol & "..."
            txtTradeResult.Foreground = New SolidColorBrush(Colors.Yellow)

            Try
                _pendingProposalAmount = amount
                Dim req As New JObject()
                req.Add("proposal", 1)
                req.Add("amount", amount)
                req.Add("basis", "stake")
                req.Add("contract_type", contractType)
                req.Add("currency", "USD")
                req.Add("duration", duration)
                req.Add("duration_unit", "t")
                req.Add("underlying_symbol", symbol)

                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)

                txtTradeResult.Text = "⏳ Esperando cotización..."
                txtTradeResult.Foreground = New SolidColorBrush(Colors.Yellow)
            Catch ex As Exception
                txtTradeResult.Text = "❌ Error: " & ex.Message
                txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
            End Try
        End Function

        ' ---- Helpers para leer controles compartidos ----
        Private Function GetSymbol() As String
            Dim item = TryCast(cmbTradingSymbol.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item?.Tag?.ToString(), "R_100")
        End Function

        Private Function GetDurUnit() As String
            Dim item = TryCast(cmbTradingDurUnit.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item?.Tag?.ToString(), "t")
        End Function

        Private Async Function EjecutarContratoTradingAsync(tipo As String) As Task
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("No hay conexión activa.")
                Return
            End If
            Dim symbol = GetSymbol()
            Dim monto As Double
            Dim duracion As Integer
            If Not Double.TryParse(txtTradingMonto.Text.Trim(), monto) OrElse monto <= 0 Then
                System.Windows.MessageBox.Show("Monto inválido.") : Return
            End If
            If Not Integer.TryParse(txtTradingDuracion.Text.Trim(), duracion) OrElse duracion <= 0 Then
                System.Windows.MessageBox.Show("Duración inválida.") : Return
            End If
            txtTradingOpEstado.Text = String.Format("⏳ Cotizando {0} {1} ${2}...", tipo, symbol, monto)
            Try
                _pendingProposalAmount = monto
                _modoAvanzado = False
                Dim req As New JObject()
                req.Add("proposal", 1)
                req.Add("amount", monto)
                req.Add("basis", "stake")
                req.Add("contract_type", tipo)
                req.Add("currency", "USD")
                req.Add("duration", duracion)
                req.Add("duration_unit", GetDurUnit())
                req.Add("underlying_symbol", symbol)
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
            Catch ex As Exception
                txtTradingOpEstado.Text = "❌ Error: " & ex.Message
            End Try
        End Function

        ' ===================================================
        '  PANEL AVANZADO
        ' ===================================================
        Private Sub expAvanzado_Expanded(sender As Object, e As System.Windows.RoutedEventArgs)
            btnTradingCall.IsEnabled = False
            btnTradingPut.IsEnabled = False
        End Sub

        Private Sub expAvanzado_Collapsed(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingWs IsNot Nothing AndAlso _tradingWs.State = WebSocketState.Open Then
                btnTradingCall.IsEnabled = True
                btnTradingPut.IsEnabled = True
            End If
            txtCotizacion.Text = "— presiona Cotizar primero —"
            _pendingProposalId = ""
            btnEjecutarAvanzado.IsEnabled = False
        End Sub

        Private Sub cmbTradingSymbol_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            If txtCotizacion IsNot Nothing Then
                txtCotizacion.Text = "— presiona Cotizar primero —"
                _pendingProposalId = ""
                If btnEjecutarAvanzado IsNot Nothing Then btnEjecutarAvanzado.IsEnabled = False
            End If
        End Sub

        Private Sub cmbContratoTipo_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            Dim item = TryCast(cmbContratoTipo.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim tipo = If(item?.Tag?.ToString(), "CALL")
            If Not txtCotizacion Is Nothing Then
                txtCotizacion.Text = "— presiona Cotizar primero —"
                _pendingProposalId = ""
                btnEjecutarAvanzado.IsEnabled = False
                Select Case tipo
                    Case "ONETOUCH", "NOTOUCH"
                        pnlBarrera.Visibility = System.Windows.Visibility.Visible
                        lblBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera.Text = "+1.5"
                        lblBarreraHint.Text = "Ej: +1.5 (relativa) o 628.50 (absoluta)"
                    Case "DIGITOVER", "DIGITUNDER", "DIGITMATCH", "DIGITDIFF"
                        pnlBarrera.Visibility = System.Windows.Visibility.Visible
                        lblBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera2.Visibility = System.Windows.Visibility.Collapsed
                        txtBarrera.Text = "5"
                        lblBarreraHint.Text = "Dígito del 0 al 9"
                    Case Else
                        pnlBarrera.Visibility = System.Windows.Visibility.Collapsed
                End Select
            End If
        End Sub

        Private Async Sub btnCotizar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("No hay conexión activa.") : Return
            End If
            Dim tipoItem = TryCast(cmbContratoTipo.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim tipo = If(tipoItem?.Tag?.ToString(), "CALL")
            Dim basisItem = TryCast(cmbTradingBasis.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Dim basis = If(basisItem?.Tag?.ToString(), "stake")
            Dim currency = If(TryCast(cmbTradingCurrency.SelectedItem, System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), "USD")
            Dim symbol = GetSymbol()
            Dim monto As Double
            Dim duracion As Integer
            If Not Double.TryParse(txtTradingMonto.Text.Trim(), monto) OrElse monto <= 0 Then
                System.Windows.MessageBox.Show("Monto inválido.") : Return
            End If
            If Not Integer.TryParse(txtTradingDuracion.Text.Trim(), duracion) OrElse duracion <= 0 Then
                System.Windows.MessageBox.Show("Duración inválida.") : Return
            End If
            txtCotizacion.Text = "⏳ Cotizando..."
            btnEjecutarAvanzado.IsEnabled = False
            _pendingProposalId = ""
            Try
                _pendingProposalAmount = monto
                _modoAvanzado = True
                Dim req As New JObject()
                req.Add("proposal", 1)
                req.Add("amount", monto)
                req.Add("basis", basis)
                req.Add("contract_type", tipo)
                req.Add("currency", currency)
                req.Add("duration", duracion)
                req.Add("duration_unit", GetDurUnit())
                req.Add("underlying_symbol", symbol)
                If pnlBarrera.Visibility = System.Windows.Visibility.Visible Then
                    Dim barVal = txtBarrera.Text.Trim()
                    If Not String.IsNullOrEmpty(barVal) Then req.Add("barrier", barVal)
                    If txtBarrera2.Visibility = System.Windows.Visibility.Visible Then
                        Dim bar2Val = txtBarrera2.Text.Trim()
                        If Not String.IsNullOrEmpty(bar2Val) Then req.Add("barrier2", bar2Val)
                    End If
                End If
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
            Catch ex As Exception
                txtCotizacion.Text = "❌ Error: " & ex.Message
            End Try
        End Sub

        Private Async Sub btnEjecutarAvanzado_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If String.IsNullOrEmpty(_pendingProposalId) Then
                System.Windows.MessageBox.Show("Cotiza primero antes de ejecutar.") : Return
            End If
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then Return
            Try
                btnEjecutarAvanzado.IsEnabled = False
                Dim buyReq As New JObject()
                buyReq.Add("buy", _pendingProposalId)
                buyReq.Add("price", _pendingProposalAmount)
                Dim bytes = Encoding.UTF8.GetBytes(buyReq.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                txtTradingOpEstado.Text = "⏳ Ejecutando orden avanzada..."
                _pendingProposalId = ""
            Catch ex As Exception
                txtTradingOpEstado.Text = "❌ Error: " & ex.Message
            End Try
        End Sub

        ' ===================================================
        '  BUCLE DE ESCUCHA CON RECONEXION AUTOMATICA
        ' ===================================================
        Private Async Function EscucharTradingAsync() As Task
            Dim buffer(32767) As Byte
            Dim intentos As Integer = 0

            Do While Not _tradingCts.IsCancellationRequested

                ' ── Bucle de recepción ──
                Try
                    Do While _tradingWs.State = WebSocketState.Open AndAlso Not _tradingCts.IsCancellationRequested
                        Dim sb As New System.Text.StringBuilder()
                        Dim result As WebSocketReceiveResult
                        Do
                            result = Await _tradingWs.ReceiveAsync(New ArraySegment(Of Byte)(buffer), _tradingCts.Token)
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))
                        Loop While Not result.EndOfMessage

                        If result.MessageType = WebSocketMessageType.Close Then
                            LogConexion("🔌 Servidor cerró la conexión (cierre limpio).")
                            Exit Do
                        End If

                        intentos = 0  ' reset contador si recibimos mensajes normales
                        ' BeginInvoke a Background priority: NO bloquea Input/Render del usuario.
                        ' Cualquier mensaje del WS se procesa cuando la UI esta idle.
                        Dim msgCopy = sb.ToString()
                        Dispatcher.BeginInvoke(Sub() ProcesarMensajeTrading(msgCopy),
                                               System.Windows.Threading.DispatcherPriority.Background)
                    Loop
                Catch ex As OperationCanceledException
                    Exit Do   ' usuario desconectó — no reconectar
                Catch ex As Exception
                    If Not _tradingCts.IsCancellationRequested Then
                        LogConexion(String.Format("⚠ Conexión caída: {0}", ex.Message))
                    End If
                End Try

                If _tradingCts.IsCancellationRequested Then Exit Do

                ' ── Reconexión automática ──
                intentos += 1
                Dim esperaSegs As Integer = If(intentos <= 3, 3, 10)  ' 3s los primeros 3 intentos, luego 10s
                LogConexion(String.Format("🔄 Reconectando en {0}s... (intento #{1})", esperaSegs, intentos))

                Dispatcher.Invoke(Sub()
                                      txtTradingEstado.Text = String.Format("⚠ Reconectando... (#{0})", intentos)
                                      txtTradingEstado.Foreground = New SolidColorBrush(Colors.Yellow)
                                      ' Marcar desconectado en el panel lateral hasta que reconecte
                                      PanelLiveSetConexion(False)
                                      btnTradingCall.IsEnabled = False
                                      btnTradingPut.IsEnabled = False
                                      btnCotizar.IsEnabled = False
                                      btnEjecutarAvanzado.IsEnabled = False
                                      btnAnalizar.IsEnabled = False
                                      btnAnalizarYOperar.IsEnabled = False
                                  End Sub)

                Await Task.Delay(esperaSegs * 1000, _tradingCts.Token)
                If _tradingCts.IsCancellationRequested Then Exit Do

                Dim reconectado As Boolean = Await ConectarTradingWsAsync(_tradingEsDemo)
                If reconectado Then
                    intentos = 0
                    LogConexion("✅ Reconexión exitosa.")
                    ' Re-suscribir contratos abiertos para recuperar P/L en vivo
                    ReSuscribirContratosAbiertos()
                Else
                    LogConexion(String.Format("❌ Reconexión #{0} fallida. Reintentando...", intentos))
                End If
            Loop
        End Function

        ''' <summary>Re-suscribe todos los contratos con estado Abierto tras una reconexión.</summary>
        Private Sub ReSuscribirContratosAbiertos()
            Dim abiertos As New List(Of String)()
            Dispatcher.Invoke(Sub()
                                  For Each op In _tradingOperaciones
                                      If op.Estado = "Abierto" AndAlso Not String.IsNullOrEmpty(op.ContractId) Then
                                          abiertos.Add(op.ContractId)
                                      End If
                                  Next
                              End Sub)
            If abiertos.Count > 0 Then
                LogConexion(String.Format("📋 Re-suscribiendo {0} contrato(s) abierto(s)...", abiertos.Count))
                For Each cid In abiertos
                    SuscribirContratoAsync(cid)
                Next
            End If
        End Sub

        ''' <summary>Escribe en el log del tab Server con timestamp.</summary>
        Private Sub LogConexion(mensaje As String)
            Dim linea = String.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), mensaje)
            Try
                AgregarLogApi(linea)
            Catch
            End Try
        End Sub

        ''' <summary>Envía ping de aplicación cada 20s además del keepalive de protocolo.</summary>
        Private Async Function PingLoopAsync() As Task
            Dim pingJson As Byte() = Encoding.UTF8.GetBytes("{""ping"":1}")
            Do While Not _tradingCts.IsCancellationRequested
                Await Task.Delay(20000, _tradingCts.Token)
                If _tradingCts.IsCancellationRequested Then Exit Do
                Try
                    If _tradingWs IsNot Nothing AndAlso _tradingWs.State = WebSocketState.Open Then
                        Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(pingJson), WebSocketMessageType.Text, True, _tradingCts.Token)
                    End If
                Catch
                End Try
            Loop
        End Function

        Private Sub ProcesarMensajeTrading(json As String)
            Try
                Dim obj = JObject.Parse(json)
                Dim msgType = obj("msg_type")?.ToString()

                ' La nueva API pone msg_type dentro del objeto error
                If String.IsNullOrEmpty(msgType) AndAlso obj("error") IsNot Nothing Then
                    msgType = obj("error")?("msg_type")?.ToString()
                End If

                If obj("error") IsNot Nothing Then
                    Dim errMsg = obj("error")?("message")?.ToString()
                    Dim errCode = obj("error")?("code")?.ToString()
                    Dim errDetails = obj("error")?("details")?.ToString()

                    ' ContractNotFound: el contrato ya cerro/expiro y no esta en open positions.
                    ' Quitamos la op del grid + BD localmente, sin ensuciar la UI.
                    If errCode = "ContractNotFound" Then
                        ' Extraer el contract_id desde el echo_req. Diferentes shapes segun el tipo de request:
                        '   - sell:  echo_req.sell = <contract_id>
                        '   - proposal_open_contract: echo_req.contract_id = <contract_id>  (o echo_req.proposal_open_contract = <id> en API vieja)
                        Dim ecoReq = obj("echo_req")
                        Dim contractIdErr As String = Nothing
                        If ecoReq IsNot Nothing Then
                            ' Shape #1: sell
                            Dim sellTok = ecoReq("sell")
                            If sellTok IsNot Nothing AndAlso sellTok.Type = JTokenType.String Then
                                contractIdErr = sellTok.ToString()
                            End If
                            ' Shape #2: contract_id directo
                            If String.IsNullOrEmpty(contractIdErr) AndAlso ecoReq("contract_id") IsNot Nothing Then
                                contractIdErr = ecoReq("contract_id").ToString()
                            End If
                            ' Shape #3: proposal_open_contract con valor != "1" (API vieja)
                            If String.IsNullOrEmpty(contractIdErr) Then
                                Dim pocTok = ecoReq("proposal_open_contract")
                                If pocTok IsNot Nothing Then
                                    Dim pocStr = pocTok.ToString()
                                    If pocStr <> "1" AndAlso pocStr <> "True" AndAlso Not String.IsNullOrEmpty(pocStr) Then
                                        contractIdErr = pocStr
                                    End If
                                End If
                            End If
                        End If
                        ' Si encontramos el contract_id, quitar la op del grid + marcar BD
                        If Not String.IsNullOrEmpty(contractIdErr) Then
                            Dim opErr = _tradingOperaciones.FirstOrDefault(Function(o) o.ContractId = contractIdErr)
                            If opErr IsNot Nothing Then
                                ' QUITAR de la coleccion (no solo cambiar estado) — el usuario quiere limpieza
                                _tradingOperaciones.Remove(opErr)
                                Dim cId = contractIdErr
                                Dim opEsLive = opErr.EsLive
                                Task.Run(Async Function() As Task
                                             Try
                                                 If opEsLive Then
                                                     Dim repoLive As New LiveTestRepository()
                                                     Await repoLive.MarcarOperacionExpiradaPorContractIdAsync(cId)
                                                 End If
                                             Catch
                                             End Try
                                         End Function)
                            End If
                        End If
                        txtTradingOpEstado.Text = String.Format("Contrato {0} ya no existe en Deriv (expirado).",
                                                               If(contractIdErr, "?"))
                        LogConexion(String.Format("[Deriv] {0} (silenciado, op limpiada)", errMsg))
                        If _pendingApiTrade IsNot Nothing Then
                            _pendingApiTrade.TrySetResult(New JObject From {{"ok", False}, {"error", errMsg}})
                            _pendingApiTrade = Nothing
                        End If
                        Return
                    End If

                    Dim fullErr = "❌ " & errMsg & If(String.IsNullOrEmpty(errCode), "", " [" & errCode & "]") &
                                  If(String.IsNullOrEmpty(errDetails), "", " — " & errDetails)
                    txtTradingOpEstado.Text = fullErr
                    txtTradeResult.Text = fullErr
                    txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
                    ' Si habia una operacion API pendiente, completarla con el error para no bloquear el servidor
                    If _pendingApiTrade IsNot Nothing Then
                        _pendingApiTrade.TrySetResult(New JObject From {{"ok", False}, {"error", fullErr}})
                        _pendingApiTrade = Nothing
                    End If
                    Return
                End If

                Select Case msgType
                    Case "pong"
                        ' Respuesta al ping de aplicación — ignorar

                    Case "balance"
                        Dim bal = obj("balance")
                        If bal IsNot Nothing Then
                            Dim balVal As Decimal = CDec(bal("balance"))
                            Dim cur As String = bal("currency")?.ToString()
                            txtTradingBalance.Text = String.Format("{0} {1:N2}", cur, balVal)
                            ' Reflejar saldo en el panel lateral siempre visible
                            PanelLiveSetSaldo(balVal, If(cur, "USD"))
                        End If

                    Case "proposal"
                        Dim propObj = obj("proposal")
                        If propObj IsNot Nothing Then
                            Dim proposalId = propObj("id")?.ToString()
                            Dim askPrice As Double = If(propObj("ask_price") IsNot Nothing, CDbl(propObj("ask_price")), _pendingProposalAmount)
                            Dim payout As Double = If(propObj("payout") IsNot Nothing, CDbl(propObj("payout")), 0)
                            Dim longcode = propObj("longcode")?.ToString()

                            If _modoAvanzado Then
                                _pendingProposalId = proposalId
                                _pendingProposalAmount = askPrice
                                Dim cotMsg = String.Format("💰 Pago: ${0:F2}  →  Ganancia potencial: ${1:F2}", askPrice, payout)
                                txtCotizacion.Text = cotMsg
                                txtTradingOpEstado.Text = If(longcode, longcode, cotMsg)
                                btnEjecutarAvanzado.IsEnabled = True
                            Else
                                txtTradingOpEstado.Text = String.Format("💱 Cotización: ${0:F2} — Ejecutando...", askPrice)
                                txtTradeResult.Text = txtTradingOpEstado.Text
                                txtTradeResult.Foreground = New SolidColorBrush(Colors.Yellow)
                                Dim buyReq As New JObject()
                                buyReq.Add("buy", proposalId)
                                buyReq.Add("price", askPrice)
                                Dim bytes = Encoding.UTF8.GetBytes(buyReq.ToString())
                                _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                            End If
                        End If

                    Case "buy"
                        Dim buyObj = obj("buy")
                        If buyObj IsNot Nothing Then
                            Dim op As New OperacionModel()
                            op.ContractId = buyObj("contract_id")?.ToString()
                            Dim shortcode = If(buyObj("shortcode")?.ToString(), "")
                            op.Tipo = If(shortcode.StartsWith("CALL"), "CALL", If(shortcode.StartsWith("PUT"), "PUT", "?"))
                            Dim parts = shortcode.Split("_"c)
                            op.Simbolo = If(parts.Length >= 3, parts(1) & "_" & parts(2), shortcode)
                            op.Monto = If(buyObj("buy_price") IsNot Nothing, CDec(buyObj("buy_price")), 0)
                            op.Estado = "Abierto"
                            op.ProfitLoss = 0
                            op.TiempoRestante = "..."
                            op.Hora = DateTime.Now.ToString("HH:mm:ss")
                            op.Duracion = ""
                            If _tradingClienteActual IsNot Nothing Then op.ClientId = _tradingClienteActual.Id
                            ' Taggear como op live si viene de una estrategia (set por ApiExecuteTrade ANTES del send)
                            If Not String.IsNullOrEmpty(_proxTradeEstrategia) Then
                                op.EsLive = True
                                op.Estrategia = _proxTradeEstrategia
                                op.SesionId = _proxTradeSesionId
                                op.OperacionLiveId = _proxTradeOpId
                            End If
                            ' Reset de la metadata para el proximo trade
                            _proxTradeEstrategia = ""
                            _proxTradeSesionId = 0
                            _proxTradeOpId = 0
                            _tradingOperaciones.Insert(0, op)
                            Dim msgBuy = String.Format("✅ {0} abierto: {1} — ${2}", op.Tipo, op.ContractId, op.Monto)
                            txtTradingOpEstado.Text = msgBuy
                            txtTradeResult.Text = msgBuy
                            txtTradeResult.Foreground = New SolidColorBrush(Colors.LightGreen)
                            GuardarOperacionBD(op)
                            SuscribirContratoAsync(op.ContractId)
                            If _pendingApiTrade IsNot Nothing Then
                                Dim apiResult = New JObject From {
                                    {"ok", True},
                                    {"contract_id", op.ContractId},
                                    {"tipo", op.Tipo},
                                    {"simbolo", op.Simbolo},
                                    {"monto", op.Monto},
                                    {"estado", op.Estado},
                                    {"hora", op.Hora}
                                }
                                _pendingApiTrade.TrySetResult(apiResult)
                                _pendingApiTrade = Nothing
                            End If
                        End If

                    Case "proposal_open_contract"
                        Dim poc = obj("proposal_open_contract")
                        If poc IsNot Nothing Then
                            Dim contractId = poc("contract_id")?.ToString()
                            Dim status = poc("status")?.ToString()
                            Dim profit As Decimal = If(poc("profit") IsNot Nothing, CDec(poc("profit")), 0)
                            Dim op = _tradingOperaciones.FirstOrDefault(Function(o) o.ContractId = contractId)
                            If op IsNot Nothing Then
                                Dim estadoPrevio = op.Estado
                                op.Estado = If(status = "won", "Ganado", If(status = "lost", "Perdido", "Abierto"))
                                op.ProfitLoss = profit
                                If poc("entry_tick") IsNot Nothing Then op.EntrySpot = poc("entry_tick").ToString()
                                If poc("current_spot") IsNot Nothing Then op.SpotActual = poc("current_spot").ToString()
                                Dim tickCount = If(poc("tick_count") IsNot Nothing, CInt(poc("tick_count")), 0)
                                Dim ticksPassed = If(poc("tick_passed") IsNot Nothing, CInt(poc("tick_passed")), 0)
                                If tickCount > 0 Then
                                    Dim ticksLeft = tickCount - ticksPassed
                                    op.TiempoRestante = If(ticksLeft > 0, ticksLeft & "t", "0t")
                                ElseIf poc("date_expiry") IsNot Nothing Then
                                    Dim expUnix = CLng(poc("date_expiry"))
                                    Dim expDt = New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expUnix)
                                    Dim secsLeft = CInt((expDt - DateTime.UtcNow).TotalSeconds)
                                    op.TiempoRestante = If(secsLeft > 0, secsLeft & "s", "0s")
                                End If

                                ' Si la operacion live acaba de cerrar (won/lost), actualizar BD
                                ' Y notificar al motor para que actualice sus contadores.
                                If op.EsLive AndAlso op.OperacionLiveId > 0 _
                                   AndAlso (status = "won" OrElse status = "lost") _
                                   AndAlso estadoPrevio <> op.Estado Then
                                    Dim opLiveId = op.OperacionLiveId
                                    Dim estadoFinal = op.Estado
                                    Dim pnlFinal = profit
                                    Dim cIdFinal = op.ContractId
                                    Dim ganada = (status = "won")
                                    Dim sidMotor = op.SesionId
                                    Dim spotSalida As Decimal = 0D
                                    Dim epochSalidaVal As Long = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                                    If poc("exit_tick") IsNot Nothing Then
                                        Decimal.TryParse(poc("exit_tick").ToString(),
                                                         Globalization.NumberStyles.Any,
                                                         Globalization.CultureInfo.InvariantCulture,
                                                         spotSalida)
                                    End If
                                    If poc("exit_tick_time") IsNot Nothing Then
                                        epochSalidaVal = CLng(poc("exit_tick_time"))
                                    End If
                                    ' 1) BD update (async)
                                    Task.Run(Async Function() As Task
                                                 Try
                                                     Dim repoLive As New LiveTestRepository()
                                                     Await repoLive.CerrarOperacionConResultadoAsync(
                                                         opLiveId, estadoFinal, pnlFinal,
                                                         spotSalida, epochSalidaVal)
                                                 Catch ex As Exception
                                                     AppLogger.Warn("[ProcesarMensajeTrading] Error cerrando op live: " & ex.Message)
                                                 End Try
                                             End Function)
                                    ' 2) Notificar al motor (si todavia esta cargado en LiveMotors)
                                    If _apiServer IsNot Nothing AndAlso sidMotor > 0 Then
                                        Dim motorLive As LiveTestMotor = Nothing
                                        SyncLock _apiServer.LiveMotors
                                            _apiServer.LiveMotors.TryGetValue(sidMotor, motorLive)
                                        End SyncLock
                                        If motorLive IsNot Nothing Then
                                            Try
                                                motorLive.NotificarOpResueltaExterna(cIdFinal, ganada, pnlFinal)
                                            Catch
                                            End Try
                                        End If
                                    End If
                                End If
                            End If
                        End If

                    Case "sell"
                        Dim sellObj = obj("sell")
                        If sellObj IsNot Nothing Then
                            Dim contractId = sellObj("contract_id")?.ToString()
                            Dim soldFor = If(sellObj("sold_for") IsNot Nothing, CDec(sellObj("sold_for")), 0D)
                            Dim op = _tradingOperaciones.FirstOrDefault(Function(o) o.ContractId = contractId)
                            If op IsNot Nothing Then
                                op.Estado = "Cerrado"
                                op.ProfitLoss = soldFor - op.Monto
                                op.TiempoRestante = "—"
                            End If
                            txtTradingOpEstado.Text = String.Format("✅ Contrato cerrado. Recibido: {0:F2}", soldFor)
                            If _pendingApiSell IsNot Nothing Then
                                _pendingApiSell.TrySetResult(New JObject From {
                                    {"ok", True}, {"contract_id", contractId}, {"sold_for", soldFor},
                                    {"profit_loss", If(op IsNot Nothing, op.ProfitLoss, 0)}
                                })
                                _pendingApiSell = Nothing
                            End If
                        End If

                End Select
            Catch
            End Try
        End Sub

        Private Sub SuscribirContratoAsync(contractId As String)
#Disable Warning BC42358
            Task.Run(Async Function()
                         Try
                             If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then Return
                             Dim req As New JObject()
                             req.Add("proposal_open_contract", 1)
                             req.Add("contract_id", contractId)
                             req.Add("subscribe", 1)
                             Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                             Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                         Catch
                         End Try
                     End Function)
#Enable Warning BC42358
        End Sub

        ''' <summary>
        ''' Quita la operacion del grid SIN tocar Deriv y la oculta en BD para que
        ''' NO vuelva a aparecer tras un reinicio.
        '''   - Marca como 'Oculto' en backtest_operaciones via contract_id (cualquier op live)
        '''   - Tambien borra la copia legacy en trading_operations si existe
        ''' </summary>
        Private Sub BtnQuitarOperacion_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim btn = TryCast(sender, System.Windows.Controls.Button)
            If btn Is Nothing Then Return
            Dim contractId = btn.Tag?.ToString()
            If String.IsNullOrEmpty(contractId) Then Return
            Dim op = _tradingOperaciones.FirstOrDefault(Function(o) o.ContractId = contractId)
            If op Is Nothing Then Return
            _tradingOperaciones.Remove(op)
            txtTradingOpEstado.Text = String.Format("Contrato {0} ocultado permanentemente.", contractId)

            ' Capturar valores antes del Task.Run
            Dim cId = contractId
            Dim opIdLive = op.OperacionLiveId
            Dim opIdMan = op.Id

            Task.Run(Async Function() As Task
                         Try
                             ' SIEMPRE intentar ocultar en backtest_operaciones por contract_id.
                             ' Esto cubre las ops cuyo OperacionLiveId no se cargo correctamente.
                             Dim repoLive As New LiveTestRepository()
                             Await repoLive.OcultarOperacionPorContractIdAsync(cId)
                             If opIdLive > 0 Then Await repoLive.OcultarOperacionPorIdAsync(opIdLive)
                         Catch
                         End Try
                         Try
                             ' Borrar copia legacy de trading_operations si existe
                             If opIdMan > 0 Then
                                 Dim repoCli As New ClientRepository()
                                 Await repoCli.EliminarOperacionAsync(opIdMan)
                             End If
                         Catch
                         End Try
                     End Function)
        End Sub

        Private Async Sub BtnCerrarOperacion_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim btn = TryCast(sender, System.Windows.Controls.Button)
            If btn Is Nothing Then Return
            Dim contractId = btn.Tag?.ToString()
            If String.IsNullOrEmpty(contractId) Then Return
            If _tradingWs Is Nothing OrElse _tradingWs.State <> WebSocketState.Open Then
                System.Windows.MessageBox.Show("No hay conexión activa.")
                Return
            End If
            Try
                Dim req As New JObject()
                req.Add("sell", contractId)
                req.Add("price", 0)
                Dim bytes = Encoding.UTF8.GetBytes(req.ToString())
                Await _tradingWs.SendAsync(New ArraySegment(Of Byte)(bytes), WebSocketMessageType.Text, True, _tradingCts.Token)
                txtTradingOpEstado.Text = "⏳ Cerrando contrato " & contractId & "..."
            Catch ex As Exception
                txtTradingOpEstado.Text = "❌ Error al cerrar: " & ex.Message
            End Try
        End Sub

        Private Sub GuardarOperacionBD(op As OperacionModel)
            Task.Run(Async Function()
                         Try
                             Dim repo As New ClientRepository()
                             Await repo.GuardarOperacionAsync(op)
                         Catch
                         End Try
                     End Function)
        End Sub

    End Class
End Namespace
