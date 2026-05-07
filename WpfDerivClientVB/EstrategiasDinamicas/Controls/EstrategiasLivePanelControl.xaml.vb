' ============================================================
' EstrategiasLivePanelControl.xaml.vb
' Panel lateral con saldo total y lista de estrategias activas.
' Disenado para mostrar el estado en tiempo real de varias
' estrategias ejecutandose en paralelo en la cuenta demo/real.
'
' Solo es la capa visual: expone metodos publicos para que el
' resto de la app le diga que mostrar y eventos para notificar
' acciones del usuario (ver detalle, pausar, detener).
'
' Sigue el patron de AdminSidebar: clase sin Namespace block,
' x:Class="EstrategiasLivePanelControl" en el XAML.
' ============================================================
Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Windows.Controls
Imports System.Windows.Media

Partial Public Class EstrategiasLivePanelControl
    Inherits UserControl

    ' ── Coleccion observable de tarjetas (cada estrategia activa = una tarjeta) ──
    Private ReadOnly _cards As New ObservableCollection(Of EstrategiaActivaCard)()

    ' ── Eventos hacia el contenedor ─────────────────────────────────
    Public Event VerDetalleSolicitado(id As String)
    Public Event PausarReanudarSolicitado(id As String)
    Public Event DetenerSolicitado(id As String)
    ''' <summary>
    ''' Disparado cuando el usuario clickea "Aplicar" en el Expander de una card.
    ''' El handler debe leer la card por Id y llamar motor.ActualizarConfig(...).
    ''' </summary>
    Public Event ConfigGuardarSolicitada(id As String, card As EstrategiaActivaCard)

    Public Sub New()
        InitializeComponent()
        lstEstrategiasActivas.ItemsSource = _cards
    End Sub

    ' ── API publica: panel superior (saldo y PnL) ───────────────────
    Public Sub ActualizarSaldo(saldoTotal As Decimal,
                               pnlHoy As Decimal,
                               pnlSemana As Decimal,
                               pnlMes As Decimal,
                               conectado As Boolean)
        txtSaldoTotal.Text = String.Format("${0:N2}", saldoTotal)
        txtPnlHoy.Text = String.Format("{0:+$0.00;-$0.00;$0.00}", pnlHoy)
        txtPnlHoy.Foreground = New SolidColorBrush(BrushPnl(pnlHoy))
        txtPnlSemana.Text = String.Format("{0:+$0.00;-$0.00;$0.00}", pnlSemana)
        txtPnlSemana.Foreground = New SolidColorBrush(BrushPnl(pnlSemana))
        txtPnlMes.Text = String.Format("{0:+$0.00;-$0.00;$0.00}", pnlMes)
        txtPnlMes.Foreground = New SolidColorBrush(BrushPnl(pnlMes))
        indConexion.Fill = New SolidColorBrush(If(conectado,
                                                  Color.FromRgb(&HA6, &HE3, &HA1),
                                                  Color.FromRgb(&HF3, &H8B, &HA8)))
    End Sub

    ''' <summary>Solo actualiza el indicador verde/rojo de conexion (no toca saldo).</summary>
    Public Sub ActualizarConexion(conectado As Boolean)
        indConexion.Fill = New SolidColorBrush(If(conectado,
                                                  Color.FromRgb(&HA6, &HE3, &HA1),
                                                  Color.FromRgb(&HF3, &H8B, &HA8)))
    End Sub

    ''' <summary>
    ''' Solo actualiza el saldo total (no toca PnL ni indicador). Si currency esta
    ''' vacio se asume USD y se muestra "$X,XXX.XX". Si viene currency se muestra "EUR 1,234.56".
    ''' </summary>
    Public Sub ActualizarSaldoTotal(saldo As Decimal, Optional currency As String = "USD")
        If String.IsNullOrWhiteSpace(currency) OrElse currency.Equals("USD", StringComparison.OrdinalIgnoreCase) Then
            txtSaldoTotal.Text = String.Format("${0:N2}", saldo)
        Else
            txtSaldoTotal.Text = String.Format("{0} {1:N2}", currency, saldo)
        End If
    End Sub

    ''' <summary>Pone el saldo en estado "desconectado" (-- gris).</summary>
    Public Sub LimpiarSaldo()
        txtSaldoTotal.Text = "--"
    End Sub

    ''' <summary>Solo actualiza los PnL HOY/SEMANA/MES (sin tocar saldo ni indicador).</summary>
    Public Sub ActualizarPnlPeriodos(pnlHoy As Decimal, pnlSemana As Decimal, pnlMes As Decimal)
        txtPnlHoy.Text = String.Format("{0:+$0.00;-$0.00;$0.00}", pnlHoy)
        txtPnlHoy.Foreground = New SolidColorBrush(BrushPnl(pnlHoy))
        txtPnlSemana.Text = String.Format("{0:+$0.00;-$0.00;$0.00}", pnlSemana)
        txtPnlSemana.Foreground = New SolidColorBrush(BrushPnl(pnlSemana))
        txtPnlMes.Text = String.Format("{0:+$0.00;-$0.00;$0.00}", pnlMes)
        txtPnlMes.Foreground = New SolidColorBrush(BrushPnl(pnlMes))
    End Sub

    ' ── API publica: gestion de tarjetas ────────────────────────────
    ''' <summary>Agrega o actualiza una tarjeta segun su Id. Si ya existe, refresca sus datos.</summary>
    Public Sub UpsertCard(card As EstrategiaActivaCard)
        If card Is Nothing OrElse String.IsNullOrEmpty(card.Id) Then Return
        Dim existente = BuscarPorId(card.Id)
        If existente IsNot Nothing Then
            Dim idx = _cards.IndexOf(existente)
            _cards(idx) = card    ' reemplazo: dispara binding
        Else
            _cards.Add(card)
        End If
        txtCountActivas.Text = _cards.Count.ToString()
    End Sub

    ''' <summary>Actualiza solo metricas de una tarjeta existente (mas eficiente que Upsert).</summary>
    Public Sub ActualizarMetricas(id As String,
                                  abiertas As Integer, ganadas As Integer, perdidas As Integer,
                                  ganadasMontoUsd As Decimal, perdidasMontoUsd As Decimal,
                                  pnl As Decimal,
                                  Optional estado As String = Nothing,
                                  Optional velasAnalizadas As Integer = -1)
        Dim c = BuscarPorId(id)
        If c Is Nothing Then Return
        c.Abiertas = abiertas
        c.Ganadas = ganadas
        c.Perdidas = perdidas
        c.GanadasMontoUsd = ganadasMontoUsd
        c.PerdidasMontoUsd = perdidasMontoUsd
        c.Pnl = pnl
        If Not String.IsNullOrEmpty(estado) Then c.Estado = estado
        If velasAnalizadas >= 0 Then c.VelasAnalizadas = velasAnalizadas
        c.Refrescar()
    End Sub

    ''' <summary>Elimina la tarjeta de la lista.</summary>
    Public Sub RemoverCard(id As String)
        Dim c = BuscarPorId(id)
        If c IsNot Nothing Then
            _cards.Remove(c)
            txtCountActivas.Text = _cards.Count.ToString()
        End If
    End Sub

    ''' <summary>Vacia la lista de tarjetas.</summary>
    Public Sub LimpiarCards()
        _cards.Clear()
        txtCountActivas.Text = "0"
    End Sub

    ''' <summary>Carga inicial de la config (al crear la tarjeta).</summary>
    Public Sub ActualizarConfigAvanzada(id As String,
                                         monto As Decimal, minConfianza As Integer,
                                         duracionVelas As Integer, payoutPct As Decimal,
                                         ventanaIndicadores As Integer, totalSenales As Integer,
                                         Optional horasOperacion As Integer = -1,
                                         Optional limiteGanancia As Decimal = -1D,
                                         Optional limitePerdida As Decimal = -1D,
                                         Optional horasTranscurridas As Decimal = -1D)
        Dim c = BuscarPorId(id)
        If c Is Nothing Then Return
        c.Monto = monto
        c.MinConfianza = minConfianza
        c.DuracionVelas = duracionVelas
        c.PayoutPct = payoutPct
        c.VentanaIndicadores = ventanaIndicadores
        c.TotalSenales = totalSenales
        If horasOperacion >= 0 Then c.HorasOperacion = horasOperacion
        If limiteGanancia >= 0D Then c.LimiteGanancia = limiteGanancia
        If limitePerdida >= 0D Then c.LimitePerdida = limitePerdida
        If horasTranscurridas >= 0D Then c.HorasTranscurridas = horasTranscurridas
        c.Refrescar()
    End Sub

    ''' <summary>Solo actualiza campos NO editables (timer): senales, payout, ventana, tiempo.</summary>
    Public Sub ActualizarSoloLectura(id As String, totalSenales As Integer, payoutPct As Decimal,
                                      ventanaIndicadores As Integer, horasTranscurridas As Decimal)
        Dim c = BuscarPorId(id)
        If c Is Nothing Then Return
        c.TotalSenales = totalSenales
        c.PayoutPct = payoutPct
        c.VentanaIndicadores = ventanaIndicadores
        c.HorasTranscurridas = horasTranscurridas
        c.Refrescar()
    End Sub

    ''' <summary>Elimina todas las tarjetas cuyo Id empieza con el prefijo dado (ej. "live-").</summary>
    Public Sub RemoverCardsConPrefijo(prefijo As String)
        If String.IsNullOrEmpty(prefijo) Then Return
        For i As Integer = _cards.Count - 1 To 0 Step -1
            If _cards(i).Id IsNot Nothing AndAlso _cards(i).Id.StartsWith(prefijo) Then
                _cards.RemoveAt(i)
            End If
        Next
        txtCountActivas.Text = _cards.Count.ToString()
    End Sub

    Private Function BuscarPorId(id As String) As EstrategiaActivaCard
        For Each c In _cards
            If c.Id = id Then Return c
        Next
        Return Nothing
    End Function

    Private Shared Function BrushPnl(valor As Decimal) As Color
        If valor > 0 Then Return Color.FromRgb(&HA6, &HE3, &HA1)
        If valor < 0 Then Return Color.FromRgb(&HF3, &H8B, &HA8)
        Return Color.FromRgb(&HCD, &HD6, &HF4)
    End Function

    ' ── Handlers de los botones de cada tarjeta ─────────────────────
    Private Sub btnVerDetalle_Click(sender As Object, e As System.Windows.RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        RaiseEvent VerDetalleSolicitado(btn.Tag?.ToString())
    End Sub

    Private Sub btnPausarReanudar_Click(sender As Object, e As System.Windows.RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        RaiseEvent PausarReanudarSolicitado(btn.Tag?.ToString())
    End Sub

    Private Sub btnDetener_Click(sender As Object, e As System.Windows.RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        RaiseEvent DetenerSolicitado(btn.Tag?.ToString())
    End Sub

    ''' <summary>Click "Aplicar" en el Expander: dispara evento con la card editada.</summary>
    Private Sub btnAplicarConfig_Click(sender As Object, e As System.Windows.RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        Dim id = btn.Tag?.ToString()
        If String.IsNullOrEmpty(id) Then Return
        Dim card = BuscarPorId(id)
        If card Is Nothing Then Return
        RaiseEvent ConfigGuardarSolicitada(id, card)
    End Sub

End Class

' =====================================================================
' DTO para cada tarjeta. Implementa INotifyPropertyChanged para que los
' bindings reaccionen cuando cambian metricas en runtime.
' =====================================================================
Public Class EstrategiaActivaCard
    Implements INotifyPropertyChanged

    Public Property Id As String
    Public Property Nombre As String

    Private _symbol As String
    Public Property Symbol As String
        Get
            Return _symbol
        End Get
        Set(value As String)
            _symbol = value
            Notify("SymbolTimeframe")
        End Set
    End Property

    Private _timeframe As String
    Public Property Timeframe As String
        Get
            Return _timeframe
        End Get
        Set(value As String)
            _timeframe = value
            Notify("SymbolTimeframe")
        End Set
    End Property

    Public Property HoraInicio As DateTime
    Public ReadOnly Property HoraInicioStr As String
        Get
            If HoraInicio = DateTime.MinValue Then Return ""
            Return HoraInicio.ToString("HH:mm")
        End Get
    End Property

    Public ReadOnly Property TiempoActivoStr As String
        Get
            If HoraInicio = DateTime.MinValue Then Return ""
            Dim ts = DateTime.Now - HoraInicio
            If ts.TotalHours >= 1 Then
                Return String.Format("{0}h {1:D2}m", CInt(Math.Floor(ts.TotalHours)), ts.Minutes)
            Else
                Return String.Format("{0}m", CInt(ts.TotalMinutes))
            End If
        End Get
    End Property

    Public ReadOnly Property SymbolTimeframe As String
        Get
            Return String.Format("{0} / {1}", If(Symbol, ""), If(Timeframe, ""))
        End Get
    End Property

    Public Property Abiertas As Integer
    Public Property Ganadas As Integer
    Public Property Perdidas As Integer
    Public Property GanadasMontoUsd As Decimal
    Public Property PerdidasMontoUsd As Decimal
    Public Property Pnl As Decimal
    Public Property VelasAnalizadas As Integer = 0

    ' ── Config avanzada (mostrada en el Expander de la card) ─────────
    Public Property Monto As Decimal = 0D
    Public Property MinConfianza As Integer = 0
    Public Property DuracionVelas As Integer = 0
    Public Property PayoutPct As Decimal = 0D
    Public Property VentanaIndicadores As Integer = 0
    Public Property TotalSenales As Integer = 0
    ' Limites editables (0 = sin limite)
    Public Property HorasOperacion As Integer = 0
    Public Property LimiteGanancia As Decimal = 0D
    Public Property LimitePerdida As Decimal = 0D
    Public Property HorasTranscurridas As Decimal = 0D

    Public ReadOnly Property HorasTranscurridasStr As String
        Get
            If HorasOperacion > 0 Then
                Return String.Format("{0:F1}h / {1}h", HorasTranscurridas, HorasOperacion)
            End If
            Return String.Format("{0:F1}h", HorasTranscurridas)
        End Get
    End Property

    Public ReadOnly Property MontoStr As String
        Get
            Return String.Format("${0:N2}", Monto)
        End Get
    End Property
    Public ReadOnly Property MinConfianzaStr As String
        Get
            Return String.Format("{0}%", MinConfianza)
        End Get
    End Property
    Public ReadOnly Property DuracionVelasStr As String
        Get
            Return String.Format("{0} velas", DuracionVelas)
        End Get
    End Property
    Public ReadOnly Property PayoutPctStr As String
        Get
            Return String.Format("{0}%", PayoutPct)
        End Get
    End Property
    Public ReadOnly Property VentanaIndicadoresStr As String
        Get
            Return String.Format("{0} velas", VentanaIndicadores)
        End Get
    End Property

    Public ReadOnly Property VelasAnalizadasStr As String
        Get
            Return String.Format("{0:N0} velas", VelasAnalizadas)
        End Get
    End Property

    Public ReadOnly Property GanadasMonto As String
        Get
            Return String.Format("${0:N2}", GanadasMontoUsd)
        End Get
    End Property
    Public ReadOnly Property PerdidasMonto As String
        Get
            Return String.Format("-${0:N2}", Math.Abs(PerdidasMontoUsd))
        End Get
    End Property
    Public ReadOnly Property PnlStr As String
        Get
            Return String.Format("{0:+0.00;-0.00;0.00}", Pnl)
        End Get
    End Property
    Public ReadOnly Property PnlBrush As Brush
        Get
            If Pnl > 0 Then Return New SolidColorBrush(Color.FromRgb(&HA6, &HE3, &HA1))
            If Pnl < 0 Then Return New SolidColorBrush(Color.FromRgb(&HF3, &H8B, &HA8))
            Return New SolidColorBrush(Color.FromRgb(&HCD, &HD6, &HF4))
        End Get
    End Property

    ''' <summary>"Activa" | "Pausada" | "Analizando" | "Detenida".</summary>
    Public Property Estado As String = "Activa"

    Public ReadOnly Property ColorEstadoBrush As Brush
        Get
            Select Case (Estado).ToLowerInvariant()
                Case "pausada"
                    Return New SolidColorBrush(Color.FromRgb(&HF9, &HE2, &HAF))
                Case "analizando"
                    Return New SolidColorBrush(Color.FromRgb(&H89, &HB4, &HFA))
                Case "detenida"
                    Return New SolidColorBrush(Color.FromRgb(&H6C, &H70, &H86))
                Case Else
                    Return New SolidColorBrush(Color.FromRgb(&HA6, &HE3, &HA1))
            End Select
        End Get
    End Property

    Public ReadOnly Property TextoBotonPausa As String
        Get
            ' ChrW para caracteres Unicode (Chr solo soporta 0-127 / codepage ANSI).
            ' 9658 = BLACK RIGHT-POINTING TRIANGLE, 10074 = HEAVY VERTICAL BAR.
            Return If(Estado = "Pausada", ChrW(9658), ChrW(10074) & ChrW(10074))
        End Get
    End Property
    Public ReadOnly Property ColorBotonPausa As Brush
        Get
            Return If(Estado = "Pausada",
                      New SolidColorBrush(Color.FromRgb(&H1E, &H3A, &H1E)),
                      New SolidColorBrush(Color.FromRgb(&H3A, &H30, &H1E)))
        End Get
    End Property
    Public ReadOnly Property ColorBordePausa As Brush
        Get
            Return If(Estado = "Pausada",
                      New SolidColorBrush(Color.FromRgb(&HA6, &HE3, &HA1)),
                      New SolidColorBrush(Color.FromRgb(&HF9, &HE2, &HAF)))
        End Get
    End Property
    Public ReadOnly Property TooltipPausa As String
        Get
            Return If(Estado = "Pausada", "Reanudar", "Pausar")
        End Get
    End Property

    ''' <summary>Notifica todos los campos derivados (forzar refresco de bindings).</summary>
    Public Sub Refrescar()
        Notify("Abiertas")
        Notify("Ganadas")
        Notify("Perdidas")
        Notify("GanadasMonto")
        Notify("PerdidasMonto")
        Notify("Pnl")
        Notify("PnlStr")
        Notify("PnlBrush")
        Notify("Estado")
        Notify("ColorEstadoBrush")
        Notify("TextoBotonPausa")
        Notify("ColorBotonPausa")
        Notify("ColorBordePausa")
        Notify("TooltipPausa")
        Notify("TiempoActivoStr")
        Notify("VelasAnalizadas")
        Notify("VelasAnalizadasStr")
        Notify("MontoStr")
        Notify("MinConfianzaStr")
        Notify("DuracionVelasStr")
        Notify("PayoutPctStr")
        Notify("VentanaIndicadoresStr")
        Notify("TotalSenales")
        Notify("Monto")
        Notify("MinConfianza")
        Notify("DuracionVelas")
        Notify("HorasOperacion")
        Notify("LimiteGanancia")
        Notify("LimitePerdida")
        Notify("HorasTranscurridasStr")
    End Sub

    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    Private Sub Notify(name As String)
        RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
    End Sub
End Class
