' ============================================================
' MainWindow.Trading.vb
' Tab "Trading Live": SOLO interfaz.
'
' La conexion con Deriv, el procesamiento de mensajes, la
' persistencia y el gestor de riesgo viven en
' Trading/DerivTradingService.vb (sin WPF). Esta ventana:
'   - llama al servicio desde los botones,
'   - se suscribe a sus eventos para mostrar estado, saldo y
'     operaciones (los eventos llegan desde hilos de fondo y se
'     pasan al Dispatcher),
'   - se desuscribe al cerrarse (el servicio sigue vivo: las
'     estrategias pueden seguir operando sin esta ventana).
' ============================================================
Imports System.Threading.Tasks
Imports System.Windows.Media
Imports Newtonsoft.Json.Linq
Imports WpfDerivClientVB.WpfDerivClientVB.Servidor

Namespace WpfDerivClientVB

    Partial Public Class MainWindow

        Private ReadOnly Property Trading As DerivTradingService
            Get
                Return DerivTradingService.Instance
            End Get
        End Property

        ' ===================================================
        '  SUSCRIPCION AL SERVICIO
        ' ===================================================
        Private Sub InicializarTradingServicio()
            AddHandler Trading.Log, AddressOf OnTradingLog
            AddHandler Trading.EstadoConexion, AddressOf OnTradingEstado
            AddHandler Trading.SaldoActualizado, AddressOf OnTradingSaldo
            AddHandler Trading.OperacionAgregada, AddressOf OnTradingOpAgregada
            AddHandler Trading.OperacionQuitada, AddressOf OnTradingOpQuitada
            AddHandler Trading.OperacionCerrada, AddressOf OnTradingOpCerrada
            AddHandler Trading.MensajeOperacion, AddressOf OnTradingMensaje
            ' Motores de la pestana Estrategias (viven en el TradingApiServer de esta ventana)
            Trading.BuscarMotorExterno = AddressOf BuscarMotorPestanaEstrategias

            ' Si el servicio ya estaba conectado (p.ej. por el modo agente), reflejarlo
            gridOperaciones.ItemsSource = _tradingOperaciones
            For Each op In Trading.ObtenerOperaciones()
                _tradingOperaciones.Add(op)
            Next
            AplicarEstadoConexion(Trading.EstaConectado,
                                  If(Trading.EstaConectado, If(Trading.EsDemo, "Demo", "Real") & " - Conectado", "Desconectado"))
            If Trading.EstaConectado Then txtTradingBalance.Text = Trading.TextoSaldo
        End Sub

        Private Sub DesuscribirTradingServicio()
            RemoveHandler Trading.Log, AddressOf OnTradingLog
            RemoveHandler Trading.EstadoConexion, AddressOf OnTradingEstado
            RemoveHandler Trading.SaldoActualizado, AddressOf OnTradingSaldo
            RemoveHandler Trading.OperacionAgregada, AddressOf OnTradingOpAgregada
            RemoveHandler Trading.OperacionQuitada, AddressOf OnTradingOpQuitada
            RemoveHandler Trading.OperacionCerrada, AddressOf OnTradingOpCerrada
            RemoveHandler Trading.MensajeOperacion, AddressOf OnTradingMensaje
            Trading.BuscarMotorExterno = Nothing
        End Sub

        Private Function BuscarMotorPestanaEstrategias(sesionId As Integer) As EstrategiasDinamicas.LiveTestMotor
            If _apiServer Is Nothing Then Return Nothing
            Dim m As EstrategiasDinamicas.LiveTestMotor = Nothing
            SyncLock _apiServer.LiveMotors
                _apiServer.LiveMotors.TryGetValue(sesionId, m)
            End SyncLock
            Return m
        End Function

        Private Sub EnUI(accion As Action)
            Dispatcher.BeginInvoke(accion, System.Windows.Threading.DispatcherPriority.Background)
        End Sub

        Private Sub OnTradingLog(linea As String)
            Try
                AgregarLogApi(linea)
            Catch
            End Try
        End Sub

        Private Sub OnTradingEstado(conectado As Boolean, texto As String)
            EnUI(Sub()
                     AplicarEstadoConexion(conectado, texto)
                     If conectado Then
                         Tareas.EnSegundoPlano(PanelLiveRefrescarPnlPeriodosAsync(), "Panel live: PnL por periodos")
                     End If
                 End Sub)
        End Sub

        Private Sub OnTradingSaldo(saldo As Decimal, moneda As String)
            EnUI(Sub()
                     txtTradingBalance.Text = String.Format("{0} {1:N2}", moneda, saldo)
                     PanelLiveSetSaldo(saldo, moneda)
                 End Sub)
        End Sub

        Private Sub OnTradingOpAgregada(op As OperacionModel)
            EnUI(Sub()
                     If Not _tradingOperaciones.Contains(op) Then
                         ' Misma posicion que en la lista del servicio (compras nuevas arriba,
                         ' operaciones recuperadas de BD en su orden)
                         Dim idx = Trading.ObtenerOperaciones().IndexOf(op)
                         _tradingOperaciones.Insert(Math.Max(0, Math.Min(idx, _tradingOperaciones.Count)), op)
                     End If
                 End Sub)
        End Sub

        Private Sub OnTradingOpQuitada(op As OperacionModel)
            EnUI(Sub() _tradingOperaciones.Remove(op))
        End Sub

        Private Sub OnTradingOpCerrada(op As OperacionModel)
            EnUI(Sub()
                     Tareas.EnSegundoPlano(PanelLiveRefrescarPnlPeriodosAsync(), "Panel live: PnL por periodos")
                 End Sub)
        End Sub

        Private Sub OnTradingMensaje(texto As String, esError As Boolean)
            EnUI(Sub()
                     Dim t = If(esError, "❌ ", "") & texto
                     txtTradingOpEstado.Text = t
                     txtTradeResult.Text = t
                     txtTradeResult.Foreground = New SolidColorBrush(If(esError, Colors.OrangeRed, Colors.LightGreen))
                 End Sub)
        End Sub

        Private Sub AplicarEstadoConexion(conectado As Boolean, texto As String)
            txtTradingEstado.Text = texto
            txtTradingEstado.Foreground = New SolidColorBrush(
                If(conectado, Colors.LightGreen,
                   If(texto.StartsWith("Reconect") OrElse texto.StartsWith("Conectando"), Colors.Yellow, Colors.OrangeRed)))
            btnConectarTrading.IsEnabled = Not conectado AndAlso Not texto.StartsWith("Conectando")
            btnDesconectarTrading.IsEnabled = conectado OrElse texto.StartsWith("Reconect")
            btnTradingCall.IsEnabled = conectado
            btnTradingPut.IsEnabled = conectado
            btnCotizar.IsEnabled = conectado
            btnAnalizar.IsEnabled = conectado
            btnAnalizarYOperar.IsEnabled = conectado
            If Not conectado Then btnEjecutarAvanzado.IsEnabled = False
            PanelLiveSetConexion(conectado)
            If Not conectado AndAlso texto = "Desconectado" Then
                txtTradingBalance.Text = "--"
                PanelLiveLimpiarSaldo()
            End If
        End Sub

        ''' <summary>Escribe en el log del tab Server con timestamp.</summary>
        Private Sub LogConexion(mensaje As String)
            Try
                AgregarLogApi(String.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), mensaje))
            Catch
            End Try
        End Sub

        ' ===================================================
        '  CLIENTES Y CONEXION
        ' ===================================================
        Private Async Sub CargarClientesTrading()
            Try
                Dim lista = Await New ClientRepository().GetAllAsync()
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
                _tradingCredsActual = Await New ClientRepository().GetDerivCredentialsAsync(_tradingClienteActual.Id)
            Catch
            End Try
        End Sub

        Private Async Sub btnConectarTrading_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If _tradingClienteActual Is Nothing Then
                System.Windows.MessageBox.Show("Selecciona un cliente primero.") : Return
            End If
            If _tradingCredsActual Is Nothing Then
                System.Windows.MessageBox.Show("El cliente no tiene credenciales Deriv configuradas.") : Return
            End If
            Dim esDemo = (cmbTradingTipoCuenta.SelectedIndex = 0)
            btnConectarTrading.IsEnabled = False
            txtTradingBalance.Text = "--"
            Dim err = Await Trading.ConectarAsync(_tradingClienteActual, _tradingCredsActual, esDemo)
            If err <> "" Then
                AplicarEstadoConexion(False, "Error de conexion")
                System.Windows.MessageBox.Show(err, "Error al conectar", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error)
            End If
        End Sub

        Private Sub btnDesconectarTrading_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Trading.Desconectar()
        End Sub

        Private Sub btnSincronizarOps_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If Not Trading.EstaConectado Then
                System.Windows.MessageBox.Show("Conecta la cuenta de trading primero.", "Sincronizar") : Return
            End If
            Dim n = Trading.SincronizarAbiertas()
            txtTradingOpEstado.Text = If(n = 0, "No hay operaciones Abierto/Pendiente para sincronizar.",
                                         String.Format("Sincronizando {0} operacion(es) con Deriv...", n))
        End Sub

        Private Async Sub btnCerrarTodasAbiertas_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If Not Trading.EstaConectado Then
                System.Windows.MessageBox.Show("Conecta la cuenta de trading primero.", "Cerrar todas") : Return
            End If
            Dim abiertas = Trading.ObtenerOperaciones().Where(Function(o) o.Estado = "Abierto" AndAlso Not String.IsNullOrEmpty(o.ContractId)).Count()
            If abiertas = 0 Then
                txtTradingOpEstado.Text = "No hay operaciones Abierto para cerrar." : Return
            End If
            Dim confirm = System.Windows.MessageBox.Show(
                String.Format("Vender {0} operacion(es) Abierto?{1}{1}Cada una se vendera al precio de mercado actual de Deriv.",
                              abiertas, Environment.NewLine),
                "Confirmar cierre masivo", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            If confirm <> System.Windows.MessageBoxResult.Yes Then Return
            txtTradingOpEstado.Text = String.Format("Cerrando {0} operaciones...", abiertas)
            Dim n = Await Trading.VenderTodasAbiertasAsync()
            txtTradingOpEstado.Text = String.Format("Comando 'sell' enviado a {0} de {1} operaciones.", n, abiertas)
        End Sub

        Private Sub BtnQuitarOperacion_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim contractId = TryCast(sender, System.Windows.Controls.Button)?.Tag?.ToString()
            If String.IsNullOrEmpty(contractId) Then Return
            Trading.QuitarOperacion(contractId)
            txtTradingOpEstado.Text = String.Format("Contrato {0} ocultado permanentemente.", contractId)
        End Sub

        Private Async Sub BtnCerrarOperacion_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Dim contractId = TryCast(sender, System.Windows.Controls.Button)?.Tag?.ToString()
            If String.IsNullOrEmpty(contractId) Then Return
            If Not Trading.EstaConectado Then
                System.Windows.MessageBox.Show("No hay conexión activa.") : Return
            End If
            txtTradingOpEstado.Text = "⏳ Cerrando contrato " & contractId & "..."
            Dim r = Await Trading.ExecuteSellAsync(contractId)
            If r("ok") Is Nothing OrElse Not CBool(r("ok")) Then
                txtTradingOpEstado.Text = "❌ Error al cerrar: " & If(r("error")?.ToString(), "")
            End If
        End Sub

        ' ===================================================
        '  OPERACIONES MANUALES
        ' ===================================================
        Private Async Sub btnTradingCall_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await OperarDesdeTabTradingAsync("CALL")
        End Sub

        Private Async Sub btnTradingPut_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await OperarDesdeTabTradingAsync("PUT")
        End Sub

        ' Botones CALL/PUT del tab Streaming (usan la misma conexion de trading)
        Private Async Sub btnBuyCall_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await OperarDesdeTabStreamingAsync("CALL")
        End Sub

        Private Async Sub btnSellPut_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            Await OperarDesdeTabStreamingAsync("PUT")
        End Sub

        Private Function GetSymbol() As String
            Dim item = TryCast(cmbTradingSymbol.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item?.Tag?.ToString(), "R_100")
        End Function

        Private Function GetDurUnit() As String
            Dim item = TryCast(cmbTradingDurUnit.SelectedItem, System.Windows.Controls.ComboBoxItem)
            Return If(item?.Tag?.ToString(), "t")
        End Function

        Private Async Function OperarDesdeTabTradingAsync(tipo As String) As Task
            If Not Trading.EstaConectado Then
                System.Windows.MessageBox.Show("No hay conexión activa.") : Return
            End If
            Dim monto As Double, duracion As Integer
            If Not Double.TryParse(txtTradingMonto.Text.Trim(), monto) OrElse monto <= 0 Then
                System.Windows.MessageBox.Show("Monto inválido.") : Return
            End If
            If Not Integer.TryParse(txtTradingDuracion.Text.Trim(), duracion) OrElse duracion <= 0 Then
                System.Windows.MessageBox.Show("Duración inválida.") : Return
            End If
            Dim symbol = GetSymbol()
            txtTradingOpEstado.Text = String.Format("⏳ Cotizando {0} {1} ${2}...", tipo, symbol, monto)
            Dim r = Await Trading.OperarManualAsync(tipo, symbol, monto, duracion, GetDurUnit())
            If r("ok") Is Nothing OrElse Not CBool(r("ok")) Then
                txtTradingOpEstado.Text = "❌ " & If(r("error")?.ToString(), "Error")
            End If
        End Function

        Private Async Function OperarDesdeTabStreamingAsync(tipo As String) As Task
            If Not Trading.EstaConectado Then
                txtTradeResult.Text = "⚡ Conecta una cuenta en el tab 'Trading Live' para operar."
                txtTradeResult.Foreground = New SolidColorBrush(Color.FromRgb(&H89, &HB4, &HFA))
                Return
            End If
            Dim symbol = txtTradeSymbol.Text.Trim()
            Dim monto As Double, duracion As Integer
            If Not Double.TryParse(txtTradeAmount.Text.Trim(), monto) OrElse monto <= 0 Then
                txtTradeResult.Text = "❌ Monto inválido." : txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed) : Return
            End If
            If Not Integer.TryParse(txtTradeDuration.Text.Trim(), duracion) OrElse duracion <= 0 Then
                txtTradeResult.Text = "❌ Duración inválida." : txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed) : Return
            End If
            txtTradeResult.Text = "⏳ Solicitando cotización " & tipo & " " & symbol & "..."
            txtTradeResult.Foreground = New SolidColorBrush(Colors.Yellow)
            Dim r = Await Trading.OperarManualAsync(tipo, symbol, monto, duracion, "t")
            If r("ok") Is Nothing OrElse Not CBool(r("ok")) Then
                txtTradeResult.Text = "❌ " & If(r("error")?.ToString(), "Error")
                txtTradeResult.Foreground = New SolidColorBrush(Colors.OrangeRed)
            End If
        End Function

        ' ===================================================
        '  PANEL AVANZADO (cotizar y luego ejecutar)
        ' ===================================================
        Private Sub expAvanzado_Expanded(sender As Object, e As System.Windows.RoutedEventArgs)
            btnTradingCall.IsEnabled = False
            btnTradingPut.IsEnabled = False
        End Sub

        Private Sub expAvanzado_Collapsed(sender As Object, e As System.Windows.RoutedEventArgs)
            btnTradingCall.IsEnabled = Trading.EstaConectado
            btnTradingPut.IsEnabled = Trading.EstaConectado
            ReiniciarCotizacion()
        End Sub

        Private Sub ReiniciarCotizacion()
            If txtCotizacion Is Nothing Then Return
            txtCotizacion.Text = "— presiona Cotizar primero —"
            _pendingProposalId = ""
            If btnEjecutarAvanzado IsNot Nothing Then btnEjecutarAvanzado.IsEnabled = False
        End Sub

        Private Sub cmbTradingSymbol_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            ReiniciarCotizacion()
        End Sub

        Private Sub cmbContratoTipo_SelectionChanged(sender As Object, e As System.Windows.Controls.SelectionChangedEventArgs)
            Dim tipo = If(TryCast(cmbContratoTipo.SelectedItem, System.Windows.Controls.ComboBoxItem)?.Tag?.ToString(), "CALL")
            If txtCotizacion Is Nothing Then Return
            ReiniciarCotizacion()
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
        End Sub

        Private Async Sub btnCotizar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If Not Trading.EstaConectado Then
                System.Windows.MessageBox.Show("No hay conexión activa.") : Return
            End If
            Dim tipo = If(TryCast(cmbContratoTipo.SelectedItem, System.Windows.Controls.ComboBoxItem)?.Tag?.ToString(), "CALL")
            Dim basis = If(TryCast(cmbTradingBasis.SelectedItem, System.Windows.Controls.ComboBoxItem)?.Tag?.ToString(), "stake")
            Dim moneda = If(TryCast(cmbTradingCurrency.SelectedItem, System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), "USD")
            Dim monto As Double, duracion As Integer
            If Not Double.TryParse(txtTradingMonto.Text.Trim(), monto) OrElse monto <= 0 Then
                System.Windows.MessageBox.Show("Monto inválido.") : Return
            End If
            If Not Integer.TryParse(txtTradingDuracion.Text.Trim(), duracion) OrElse duracion <= 0 Then
                System.Windows.MessageBox.Show("Duración inválida.") : Return
            End If
            Dim barrera As String = Nothing, barrera2 As String = Nothing
            If pnlBarrera.Visibility = System.Windows.Visibility.Visible Then
                barrera = txtBarrera.Text.Trim()
                If txtBarrera2.Visibility = System.Windows.Visibility.Visible Then barrera2 = txtBarrera2.Text.Trim()
            End If

            txtCotizacion.Text = "⏳ Cotizando..."
            btnEjecutarAvanzado.IsEnabled = False
            _pendingProposalId = ""
            Dim r = Await Trading.CotizarAsync(tipo, GetSymbol(), monto, duracion, GetDurUnit(), basis, moneda, barrera, barrera2)
            If r("ok") IsNot Nothing AndAlso CBool(r("ok")) Then
                _pendingProposalId = r("id").ToString()
                _pendingProposalAmount = CDbl(r("ask_price"))
                Dim cotMsg = String.Format("💰 Pago: ${0:F2}  →  Ganancia potencial: ${1:F2}", _pendingProposalAmount, CDbl(r("payout")))
                txtCotizacion.Text = cotMsg
                Dim longcode = r("longcode")?.ToString()
                txtTradingOpEstado.Text = If(String.IsNullOrEmpty(longcode), cotMsg, longcode)
                btnEjecutarAvanzado.IsEnabled = True
            Else
                txtCotizacion.Text = "❌ " & If(r("error")?.ToString(), "Error")
            End If
        End Sub

        Private Async Sub btnEjecutarAvanzado_Click(sender As Object, e As System.Windows.RoutedEventArgs)
            If String.IsNullOrEmpty(_pendingProposalId) Then
                System.Windows.MessageBox.Show("Cotiza primero antes de ejecutar.") : Return
            End If
            btnEjecutarAvanzado.IsEnabled = False
            txtTradingOpEstado.Text = "⏳ Ejecutando orden avanzada..."
            Dim id = _pendingProposalId
            _pendingProposalId = ""
            Dim r = Await Trading.ComprarCotizacionAsync(id, _pendingProposalAmount)
            If r("ok") Is Nothing OrElse Not CBool(r("ok")) Then
                txtTradingOpEstado.Text = "❌ " & If(r("error")?.ToString(), "Error")
            End If
        End Sub

    End Class
End Namespace
