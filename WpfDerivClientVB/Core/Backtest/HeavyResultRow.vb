' ============================================================
' HeavyResultRow.vb
'
' DTO de una fila del DataGrid central del Heavy Strategy Tester.
' Construido a partir de un HeavyTestJob completado y su BacktestResultado.
' ============================================================
Imports System.ComponentModel
Imports System.Windows.Media

Namespace WpfDerivClientVB.Backtesting

    Public Class HeavyResultRow
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
        Private Sub Notify(name As String)
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

        ''' <summary>Marcado por el usuario via checkbox en el DataGrid.</summary>
        Private _selected As Boolean = False
        Public Property Selected As Boolean
            Get
                Return _selected
            End Get
            Set(value As Boolean)
                If _selected = value Then Return
                _selected = value
                Notify("Selected")
            End Set
        End Property

        ''' <summary>Id de la sesion en BD (0 si aun no esta persistida).</summary>
        Public Property SesionId As Integer = 0
        Public Property Rank As Integer = 0
        Public Property Estrategia As String = ""
        Public Property Activo As String = ""
        Public Property Timeframe As String = ""
        Public Property Conf As Integer = 0
        Public Property Pay As Decimal = 0D
        Public Property Dur As Integer = 0
        Public Property Cap As Decimal = 0D
        Public Property Ops As Integer = 0
        Public Property OpsSem As Decimal = 0D
        Public Property Win As Decimal = 0D       ' 0..100 (porcentaje)
        Public Property Pnl As Decimal = 0D
        Public Property DD As Decimal = 0D        ' 0..100 (porcentaje)
        Public Property Sharpe As Decimal = 0D
        Public Property Estado As String = ""
        Public Property FechaInicio As DateTime = DateTime.MinValue
        Public Property FechaFin As DateTime = DateTime.MinValue

        ''' <summary>True si Pay es el payout real medido en Deriv (no un valor supuesto).</summary>
        Public Property PayReal As Boolean = False

        ' ── Periodo ciego: operaciones con entrada desde la fecha de corte ──
        Public Property CiegoOps As Integer = 0
        Public Property CiegoGanadas As Integer = 0
        Public Property CiegoIcInf As Double = 0
        Public Property CiegoIcSup As Double = 0
        ''' <summary>Veredicto del periodo ciego ("" = sin fecha de corte).</summary>
        Public Property Veredicto As String = ""

        ''' <summary>Calcula el intervalo y el veredicto del periodo ciego con el payout de la fila.</summary>
        Public Sub AplicarCiego(operaciones As Integer, ganadas As Integer, hayCorte As Boolean)
            CiegoOps = operaciones
            CiegoGanadas = ganadas
            If hayCorte Then
                Dim ic = Estadistica.IntervaloWilson(ganadas, operaciones)
                CiegoIcInf = ic.Item1 : CiegoIcSup = ic.Item2
                ' Sin operaciones desde el corte: el backtest no cubre el periodo ciego
                Veredicto = If(operaciones = 0, "SIN DATOS", Estadistica.Veredicto(ganadas, operaciones, Pay))
            Else
                CiegoIcInf = 0 : CiegoIcSup = 0 : Veredicto = ""
            End If
            For Each n In {"CiegoOps", "CiegoOpsStr", "CiegoWinStr", "CiegoIcStr", "VeredictoStr", "VeredictoTip", "VeredictoBrush"}
                Notify(n)
            Next
        End Sub

        ''' <summary>(operaciones, ganadas) con entrada en o despues de corteEpoch.</summary>
        Public Shared Function ContarCiego(ops As IEnumerable(Of BacktestOperacion), corteEpoch As Long) As Tuple(Of Integer, Integer)
            If ops Is Nothing OrElse corteEpoch <= 0 Then Return Tuple.Create(0, 0)
            Dim ciego = ops.Where(Function(o) o.EpochEntrada >= corteEpoch AndAlso (o.Estado = "Ganado" OrElse o.Estado = "Perdido")).ToList()
            Return Tuple.Create(ciego.Count, ciego.Where(Function(o) o.Estado = "Ganado").Count())
        End Function

        Public ReadOnly Property CiegoOpsStr As String
            Get
                Return If(Veredicto = "", "", CiegoOps.ToString())
            End Get
        End Property
        Public ReadOnly Property CiegoWinStr As String
            Get
                If Veredicto = "" OrElse CiegoOps = 0 Then Return ""
                Return (100.0 * CiegoGanadas / CiegoOps).ToString("F1") & "%"
            End Get
        End Property
        Public ReadOnly Property CiegoIcStr As String
            Get
                If Veredicto = "" OrElse CiegoOps = 0 Then Return ""
                Return (CiegoIcInf * 100).ToString("F0") & "-" & (CiegoIcSup * 100).ToString("F0") & "%"
            End Get
        End Property
        Public ReadOnly Property VeredictoStr As String
            Get
                Select Case Veredicto
                    Case "VENTAJA ESTADISTICA" : Return "Ventaja"
                    Case "POSITIVO, NO SIGNIFICATIVO" : Return "No significativo"
                    Case "MUESTRA INSUFICIENTE" : Return "Muestra insuficiente"
                    Case "SIN VENTAJA" : Return "Sin ventaja"
                    Case "SIN DATOS" : Return "Sin datos ciegos"
                    Case Else : Return ""
                End Select
            End Get
        End Property
        Public ReadOnly Property VeredictoBrush As Brush
            Get
                Select Case Veredicto
                    Case "VENTAJA ESTADISTICA" : Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
                    Case "POSITIVO, NO SIGNIFICATIVO" : Return New SolidColorBrush(Color.FromRgb(&HF2, &HC1, &H4E))
                    Case "SIN VENTAJA" : Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
                    Case Else : Return New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
                End Select
            End Get
        End Property
        Public ReadOnly Property VeredictoTip As String
            Get
                If Veredicto = "" Then Return "Indica una fecha de corte para medir el periodo ciego."
                If Veredicto = "SIN DATOS" Then Return "Este backtest no tiene operaciones desde la fecha de corte (su periodo terminaba antes o no habia velas). Vuelve a ejecutarlo con un periodo que incluya el ciego."
                Return String.Format("Periodo ciego: {0} operaciones, minimo para no perder {1:P1} (payout {2}%). " &
                                     "Ventaja = el limite inferior del IC95 supera ese minimo.",
                                     CiegoOps, Estadistica.PuntoEquilibrio(Pay), Pay)
            End Get
        End Property

        ''' <summary>Referencia al job para abrir el drawer al hacer click.</summary>
        Public Property JobRef As HeavyTestJob = Nothing

        ' ── Properties calculadas para presentacion ──────────────────
        Public ReadOnly Property RankStr As String
            Get
                Return "#" & Rank
            End Get
        End Property
        Public ReadOnly Property ConfStr As String
            Get
                Return Conf & "%"
            End Get
        End Property
        Public ReadOnly Property PayStr As String
            Get
                Return If(PayReal, Pay.ToString("F1") & "% real", Pay.ToString("F0") & "%")
            End Get
        End Property
        Public ReadOnly Property CapStr As String
            Get
                Return "$" & Cap.ToString("N0")
            End Get
        End Property
        Public ReadOnly Property OpsSemStr As String
            Get
                Return OpsSem.ToString("F1")
            End Get
        End Property
        Public ReadOnly Property WinStr As String
            Get
                Return Win.ToString("F1") & "%"
            End Get
        End Property
        Public ReadOnly Property PnlStr As String
            Get
                Return Pnl.ToString("+0.00;-0.00;0.00")
            End Get
        End Property
        Public ReadOnly Property DDStr As String
            Get
                Return DD.ToString("F1") & "%"
            End Get
        End Property
        Public ReadOnly Property SharpeStr As String
            Get
                Return Sharpe.ToString("F2")
            End Get
        End Property
        Public ReadOnly Property InicioStr As String
            Get
                If FechaInicio = DateTime.MinValue Then Return ""
                Return FechaInicio.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
        Public ReadOnly Property FinStr As String
            Get
                If FechaFin = DateTime.MinValue Then Return ""
                Return FechaFin.ToLocalTime().ToString("HH:mm:ss")
            End Get
        End Property
        Public ReadOnly Property DuracionStr As String
            Get
                If FechaInicio = DateTime.MinValue OrElse FechaFin = DateTime.MinValue Then Return ""
                Dim ts = FechaFin - FechaInicio
                If ts.TotalSeconds < 0 Then Return ""
                If ts.TotalMinutes < 1 Then Return ts.Seconds & "s"
                If ts.TotalHours < 1 Then Return ts.Minutes & "m " & ts.Seconds & "s"
                Return CInt(Math.Floor(ts.TotalHours)) & "h " & ts.Minutes & "m"
            End Get
        End Property
        Public ReadOnly Property FechaInicioStr As String
            Get
                If FechaInicio = DateTime.MinValue Then Return ""
                Return FechaInicio.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            End Get
        End Property
        Public ReadOnly Property FechaFinStr As String
            Get
                If FechaFin = DateTime.MinValue Then Return ""
                Return FechaFin.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            End Get
        End Property

        Public ReadOnly Property PnlBrush As Brush
            Get
                If Pnl > 0D Then Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
                If Pnl < 0D Then Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
                Return New SolidColorBrush(Color.FromRgb(&HAA, &HB3, &HC5))
            End Get
        End Property

        Public ReadOnly Property WinBrush As Brush
            Get
                If Win >= 50D Then Return New SolidColorBrush(Color.FromRgb(&H3F, &HD1, &H7A))
                Return New SolidColorBrush(Color.FromRgb(&HFF, &H5C, &H7A))
            End Get
        End Property

        ''' <summary>True si la combinacion es "ganadora": win >= 50% AND pnl > 0.</summary>
        Public ReadOnly Property EsGanadora As Boolean
            Get
                Return Win >= 50D AndAlso Pnl > 0D
            End Get
        End Property

        ''' <summary>Construye una fila a partir de un job completado.</summary>
        Public Shared Function DesdeJob(job As HeavyTestJob) As HeavyResultRow
            Dim r As New HeavyResultRow With {
                .Estrategia = job.Estrategia,
                .Activo = job.Symbol,
                .Timeframe = job.Timeframe,
                .Conf = job.Confianza,
                .Pay = job.Payout,
                .Dur = job.Duracion,
                .Cap = job.Capital,
                .Estado = job.EstadoStr,
                .FechaInicio = job.FechaInicio,
                .FechaFin = job.FechaFin,
                .JobRef = job,
                .PayReal = job.PayoutEsReal
            }
            If job.Resultado IsNot Nothing AndAlso job.Resultado.Sesion IsNot Nothing Then
                Dim ses = job.Resultado.Sesion
                r.SesionId = ses.Id
                r.Ops = ses.TotalOperaciones
                r.Win = ses.WinRate * 100D
                r.Pnl = ses.ProfitLossTotal
                r.DD = ses.MaxDrawdown
                r.OpsSem = HeavyStatsCalculator.OpsPromedioSemanal(job.Resultado.Operaciones)
                r.Sharpe = HeavyStatsCalculator.Sharpe(job.Resultado.Operaciones)
            End If
            Return r
        End Function

        ''' <summary>
        ''' Construye una fila a partir de una sesion ya persistida en BD (sin operaciones cargadas).
        ''' Sharpe/OpsSem quedan en 0 hasta que el usuario abra el drawer y se carguen las ops.
        ''' </summary>
        Public Shared Function DesdeSesion(s As BacktestSesion) As HeavyResultRow
            If s Is Nothing Then Return Nothing
            Dim r As New HeavyResultRow With {
                .SesionId = s.Id,
                .Estrategia = s.Estrategia,
                .Activo = s.Symbol,
                .Timeframe = s.Timeframe,
                .Conf = s.MinConfianza,
                .Pay = s.PayoutPct,
                .Dur = s.DuracionVelas,
                .Cap = 0D,
                .Ops = s.TotalOperaciones,
                .Win = s.WinRate * 100D,
                .Pnl = s.ProfitLossTotal,
                .DD = s.MaxDrawdown,
                .Estado = "GUARDADA",
                .FechaInicio = s.CreatedAt,
                .FechaFin = s.CreatedAt
            }
            Return r
        End Function
    End Class

End Namespace
