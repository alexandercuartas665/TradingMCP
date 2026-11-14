' ============================================================
' HeavyOperacionesWindow.xaml.vb
'
' Ventana modal que muestra todas las operaciones de una sesion de
' backtest persistida en BD. Carga vias BacktestRepository y permite
' filtrar por estado (todas/ganadas/perdidas) + exportar CSV.
' ============================================================
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Media
Imports WpfDerivClientVB.WpfDerivClientVB
Imports WpfDerivClientVB.WpfDerivClientVB.Backtesting
Imports WpfDerivClientVB.WpfDerivClientVB.EstrategiasDinamicas

Partial Public Class HeavyOperacionesWindow
    Inherits Window

    Private ReadOnly _repo As New BacktestRepository()
    Private _todas As New List(Of OpRowVm)()
    Private _filtroActual As String = "all"
    Private _sesion As BacktestSesion = Nothing

    Public Sub New()
        InitializeComponent()
    End Sub

    ''' <summary>Carga la sesion + sus operaciones en la grilla. Llamar antes de Show().</summary>
    Public Async Function CargarAsync(sesionId As Integer) As Task
        If sesionId <= 0 Then Return
        txtStatus.Text = "Cargando operaciones de sesion #" & sesionId & "..."
        Try
            _sesion = Await _repo.GetSesionPorIdAsync(sesionId)
            Dim ops = Await _repo.GetOperacionesPorSesionAsync(sesionId)
            If _sesion Is Nothing Then
                txtStatus.Text = "No se encontro la sesion #" & sesionId
                Return
            End If

            ' Title de ventana y header
            Me.Title = String.Format("Detalle Operaciones · #{0} · {1} · {2}/{3}",
                                      _sesion.Id, _sesion.Estrategia, _sesion.Symbol, _sesion.Timeframe)
            txtTitulo.Text = String.Format("Sesion #{0} · {1}", _sesion.Id, _sesion.Estrategia)
            txtSub.Text = String.Format("Activo {0} · Timeframe {1}", _sesion.Symbol, _sesion.Timeframe)

            ' Panel de info de la estrategia
            Dim defReg = EstrategiasDinamicasRegistry.GetByNombre(_sesion.Estrategia)
            Dim version = If(defReg IsNot Nothing AndAlso defReg.Definicion IsNot Nothing,
                             " (v" & defReg.Definicion.Version & ")", "")
            infoEstrategia.Text = _sesion.Estrategia & version
            infoSymbolTf.Text = String.Format("{0}  /  {1}", _sesion.Symbol, _sesion.Timeframe)
            infoConf.Text = _sesion.MinConfianza & " %"
            infoPay.Text = _sesion.PayoutPct.ToString("F2") & " %"
            infoDur.Text = _sesion.DuracionVelas & " velas"

            ' Periodo
            If _sesion.DesdeEpoch > 0 Then
                infoDesde.Text = DateTimeOffset.FromUnixTimeSeconds(_sesion.DesdeEpoch).
                                  LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            Else
                infoDesde.Text = "(completo)"
            End If
            If _sesion.HastaEpoch > 0 Then
                infoHasta.Text = DateTimeOffset.FromUnixTimeSeconds(_sesion.HastaEpoch).
                                  LocalDateTime.ToString("yyyy-MM-dd HH:mm")
            Else
                infoHasta.Text = "(completo)"
            End If
            If _sesion.DesdeEpoch > 0 AndAlso _sesion.HastaEpoch > 0 Then
                Dim rangoSegs = _sesion.HastaEpoch - _sesion.DesdeEpoch
                Dim dias = rangoSegs / 86400.0
                If dias < 1 Then
                    infoDuracionRango.Text = (rangoSegs / 3600.0).ToString("F1") & " h"
                ElseIf dias < 60 Then
                    infoDuracionRango.Text = dias.ToString("F1") & " días"
                Else
                    infoDuracionRango.Text = (dias / 30.0).ToString("F1") & " meses"
                End If
            Else
                infoDuracionRango.Text = "—"
            End If

            ' Ejecucion
            infoId.Text = "#" & _sesion.Id
            infoEjecutada.Text = _sesion.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            infoNotas.Text = If(String.IsNullOrWhiteSpace(_sesion.Notas), "(sin notas)", _sesion.Notas)

            ' KPIs
            kTotalOps.Text = _sesion.TotalOperaciones.ToString("N0")
            Dim ganadas = ops.AsEnumerable().Count(Function(o) o.Estado = "Ganado")
            Dim perdidas = ops.AsEnumerable().Count(Function(o) o.Estado = "Perdido")
            kGanadas.Text = ganadas.ToString("N0")
            kPerdidas.Text = perdidas.ToString("N0")
            kWin.Text = (_sesion.WinRate * 100D).ToString("F2") & "%"
            kPnl.Text = _sesion.ProfitLossTotal.ToString("+0.00;-0.00;0.00")
            kPnl.Foreground = If(_sesion.ProfitLossTotal >= 0D,
                                 New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A)),
                                 New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A)))
            kDD.Text = _sesion.MaxDrawdown.ToString("F2") & "%"

            ' Filas
            _todas.Clear()
            Dim idx As Integer = 1
            For Each o In ops.OrderBy(Function(x) x.EpochEntrada)
                _todas.Add(New OpRowVm With {
                    .Idx = idx,
                    .Apertura = o.EpochEntrada,
                    .Cierre = o.EpochSalida,
                    .Tipo = o.Tipo,
                    .Entrada = o.PrecioEntrada,
                    .Salida = o.PrecioSalida,
                    .Monto = o.Monto,
                    .Estado = o.Estado,
                    .Pnl = o.ProfitLoss,
                    .Balance = o.BalancePost,
                    .Confianza = o.Confianza,
                    .MotivoSenal = If(o.MotivoSenal, "")
                })
                idx += 1
            Next

            AplicarFiltro()
            txtStatus.Text = String.Format("{0:N0} operaciones cargadas. {1:N0} ganadas, {2:N0} perdidas.",
                                           ops.Count, ganadas, perdidas)
        Catch ex As Exception
            txtStatus.Text = "Error: " & ex.Message
        End Try
    End Function

    Private Sub AplicarFiltro()
        Dim base = _todas.AsEnumerable()
        Select Case _filtroActual
            Case "win" : base = base.Where(Function(o) o.Estado = "Ganado")
            Case "loss" : base = base.Where(Function(o) o.Estado = "Perdido")
        End Select
        Dim lista = base.ToList()
        dgOperaciones.ItemsSource = lista
        txtFiltroInfo.Text = String.Format("Mostrando {0:N0} de {1:N0}", lista.Count, _todas.Count)
    End Sub

    Private Sub btnFiltro_Click(sender As Object, e As RoutedEventArgs)
        Dim btn = TryCast(sender, Button)
        If btn Is Nothing Then Return
        _filtroActual = btn.Tag?.ToString()
        AplicarFiltro()
    End Sub

    Private Sub btnCerrar_Click(sender As Object, e As RoutedEventArgs)
        Me.Close()
    End Sub

    Private Sub btnExportarCsv_Click(sender As Object, e As RoutedEventArgs)
        If _todas.Count = 0 Then
            txtStatus.Text = "No hay operaciones para exportar."
            Return
        End If
        Try
            Dim baseDir = AppDomain.CurrentDomain.BaseDirectory
            Dim outDir = Path.Combine(baseDir, "backups")
            If Not Directory.Exists(outDir) Then Directory.CreateDirectory(outDir)
            Dim ts = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            Dim nombre = "ops_sesion"
            If _sesion IsNot Nothing Then nombre &= "_" & _sesion.Id
            Dim archivo = Path.Combine(outDir, nombre & "_" & ts & ".csv")

            Using sw As New StreamWriter(archivo, False, System.Text.Encoding.UTF8)
                sw.WriteLine("#,Apertura,Cierre,Tipo,Entrada,Salida,Monto,Estado,PnL,Balance,Conf%,Motivo")
                For Each o In _todas
                    sw.WriteLine(String.Join(",", New String() {
                        o.Idx.ToString(),
                        o.AperturaStr,
                        o.CierreStr,
                        Esc(o.Tipo),
                        o.Entrada.ToString("F5"),
                        o.Salida.ToString("F5"),
                        o.Monto.ToString("F2"),
                        Esc(o.Estado),
                        o.Pnl.ToString("F2"),
                        o.Balance.ToString("F2"),
                        o.Confianza.ToString(),
                        Esc(o.MotivoSenal)
                    }))
                Next
            End Using
            txtStatus.Text = "CSV exportado: " & archivo
        Catch ex As Exception
            txtStatus.Text = "Error exportando: " & ex.Message
        End Try
    End Sub

    Private Function Esc(s As String) As String
        If s Is Nothing Then Return ""
        If s.Contains(",") OrElse s.Contains("""") Then
            Return """" & s.Replace("""", """""") & """"
        End If
        Return s
    End Function

End Class

''' <summary>VM por fila para mostrar una operacion con formato.</summary>
Public Class OpRowVm
    Public Property Idx As Integer
    Public Property Apertura As Long
    Public Property Cierre As Long
    Public Property Tipo As String
    Public Property Entrada As Decimal
    Public Property Salida As Decimal
    Public Property Monto As Decimal
    Public Property Estado As String
    Public Property Pnl As Decimal
    Public Property Balance As Decimal
    Public Property Confianza As Integer
    Public Property MotivoSenal As String

    Public ReadOnly Property AperturaStr As String
        Get
            If Apertura <= 0 Then Return ""
            Return DateTimeOffset.FromUnixTimeSeconds(Apertura).LocalDateTime.ToString("dd/MM/yy HH:mm:ss")
        End Get
    End Property
    Public ReadOnly Property CierreStr As String
        Get
            If Cierre <= 0 Then Return ""
            Return DateTimeOffset.FromUnixTimeSeconds(Cierre).LocalDateTime.ToString("dd/MM/yy HH:mm:ss")
        End Get
    End Property
    Public ReadOnly Property EntradaStr As String
        Get
            Return Entrada.ToString("F5")
        End Get
    End Property
    Public ReadOnly Property SalidaStr As String
        Get
            If Salida <= 0D Then Return ""
            Return Salida.ToString("F5")
        End Get
    End Property
    Public ReadOnly Property MontoStr As String
        Get
            Return "$" & Monto.ToString("N2")
        End Get
    End Property
    Public ReadOnly Property PnlStr As String
        Get
            Return Pnl.ToString("+0.00;-0.00;0.00")
        End Get
    End Property
    Public ReadOnly Property BalanceStr As String
        Get
            Return "$" & Balance.ToString("N2")
        End Get
    End Property
    Public ReadOnly Property ConfianzaStr As String
        Get
            Return Confianza & "%"
        End Get
    End Property
    Public ReadOnly Property PnlBrush As Brush
        Get
            If Pnl > 0D Then Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
            If Pnl < 0D Then Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
            Return New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
        End Get
    End Property
    Public ReadOnly Property EstadoBrush As Brush
        Get
            If Estado = "Ganado" Then Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
            If Estado = "Perdido" Then Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
            Return New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
        End Get
    End Property
End Class
