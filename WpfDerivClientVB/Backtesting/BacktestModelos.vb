Imports System.Collections.Generic

Namespace WpfDerivClientVB.Backtesting

    ''' <summary>
    ''' Cuenta simulada para el backtest.
    ''' Cada cuenta tiene su propio capital y lleva el conteo de operaciones.
    ''' </summary>
    Public Class BacktestCuenta
        Public Property NombreCuenta      As String
        Public Property BalanceInicial    As Decimal
        Public Property BalanceActual     As Decimal
        Public Property MontoPorOperacion As Decimal
        Public Property NGanadas          As Integer
        Public Property NPerdidas         As Integer
        Public Property NNeutras          As Integer

        Public ReadOnly Property TotalOperaciones As Integer
            Get
                Return NGanadas + NPerdidas + NNeutras
            End Get
        End Property

        Public ReadOnly Property WinRate As Decimal
            Get
                Dim total = NGanadas + NPerdidas
                If total = 0 Then Return 0D
                Return CDec(NGanadas) / CDec(total)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Configuracion de una sesion de backtesting.
    ''' </summary>
    Public Class BacktestConfig
        Public Property Symbol              As String
        Public Property Timeframe           As String
        Public Property Platform            As String  = "deriv"
        Public Property DesdeEpoch          As Long
        Public Property HastaEpoch          As Long
        ''' <summary>Cuantas velas esperar antes de evaluar el resultado del trade.</summary>
        Public Property DuracionVelas       As Integer = 5
        ''' <summary>Payout de opciones binarias, ej: 85.0 significa ganar el 85% del monto apostado.</summary>
        Public Property PayoutPct           As Decimal = 85D
        ''' <summary>Confianza minima para abrir un trade (0-100).</summary>
        Public Property MinConfianza        As Integer = 55
        ''' <summary>Cuantas velas de historia recibe la estrategia en cada paso.</summary>
        Public Property VentanaIndicadores  As Integer = 100
        ''' <summary>Cuantas velas previas incluir en el contexto de una perdida.</summary>
        Public Property GuardarContextoVelas As Integer = 5
        Public Property Cuentas             As New List(Of BacktestCuenta)
    End Class

    ''' <summary>
    ''' Registro de una operacion simulada dentro de un backtest.
    ''' </summary>
    Public Class BacktestOperacion
        Public Property SesionId          As Integer
        Public Property CuentaNombre      As String
        Public Property Tipo              As String    ' CALL | PUT
        Public Property EpochEntrada      As Long
        Public Property PrecioEntrada     As Decimal
        Public Property EpochSalida       As Long
        Public Property PrecioSalida      As Decimal
        Public Property Monto             As Decimal
        Public Property PayoutPct         As Decimal
        ''' <summary>Ganado | Perdido | Neutro (sin vela de salida disponible)</summary>
        Public Property Estado            As String
        Public Property ProfitLoss        As Decimal
        Public Property BalancePost       As Decimal
        Public Property Confianza         As Integer
        Public Property MotivoSenal       As String
        ''' <summary>JSON serializado de EstrategiaResultado.Datos. Presente en todas las operaciones.</summary>
        Public Property DatosIndicadores  As String = ""
        ''' <summary>JSON con OHLC de las velas previas. Solo se rellena cuando Estado = "Perdido".</summary>
        Public Property ContextoVelas     As String = ""
    End Class

    ''' <summary>
    ''' Metadata y metricas agregadas de una sesion de backtest guardada en BD.
    ''' </summary>
    Public Class BacktestSesion
        Public Property Id                As Integer
        Public Property Estrategia        As String
        Public Property Symbol            As String
        Public Property Timeframe         As String
        Public Property DesdeEpoch        As Long
        Public Property HastaEpoch        As Long
        Public Property DuracionVelas     As Integer
        Public Property PayoutPct         As Decimal
        Public Property MinConfianza      As Integer
        Public Property TotalSenales      As Integer
        Public Property TotalOperaciones  As Integer
        Public Property WinRate           As Decimal
        Public Property ProfitLossTotal   As Decimal
        Public Property MaxDrawdown       As Decimal
        Public Property ProfitFactor      As Decimal
        Public Property CreatedAt         As DateTime
        Public Property Notas             As String = ""
    End Class

    ''' <summary>
    ''' Resultado completo devuelto por BacktestMotor.EjecutarAsync.
    ''' </summary>
    Public Class BacktestResultado
        Public Property Sesion         As BacktestSesion
        Public Property Operaciones    As New List(Of BacktestOperacion)
        Public Property CuentasFinales As New List(Of BacktestCuenta)

        Public ReadOnly Property OperacionesPerdidas As List(Of BacktestOperacion)
            Get
                Return Operaciones.FindAll(Function(o) o.Estado = "Perdido")
            End Get
        End Property

        Public ReadOnly Property OperacionesGanadas As List(Of BacktestOperacion)
            Get
                Return Operaciones.FindAll(Function(o) o.Estado = "Ganado")
            End Get
        End Property
    End Class

End Namespace
