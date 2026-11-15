' ============================================================
' EstrategiaJson.vb
'
' Implementa IEstrategia a partir de una definicion JSON cargada
' desde disco. Las condiciones se compilan una sola vez en el
' constructor para maxima eficiencia en la evaluacion por vela.
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Newtonsoft.Json
Imports WpfDerivClientVB.WpfDerivClientVB.Indicators
Imports WpfDerivClientVB.WpfDerivClientVB.Estrategias

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Estrategia dinamica definida por un archivo JSON.
    ''' Implementa IEstrategia para ser usada directamente con BacktestMotor.
    ''' </summary>
    Public Class EstrategiaJson
        Implements IEstrategia

        ' ── Definicion y delegates compilados ───────────────────────────
        Private ReadOnly _def      As EstrategiaDinamicaDef
        Private ReadOnly _fnCall   As Func(Of EvalContext, Boolean)
        Private ReadOnly _fnPut    As Func(Of EvalContext, Boolean)
        Private ReadOnly _ajustes  As List(Of ValueTuple(Of Func(Of EvalContext, Boolean), Integer))
        Private ReadOnly _indicadores As List(Of IIndicator)  ' pre-instanciados

        ''' <summary>
        ''' Crea la estrategia desde una definicion deserializada.
        ''' Compila todas las condiciones y crea los indicadores en el constructor.
        ''' </summary>
        Public Sub New(def As EstrategiaDinamicaDef)
            If def Is Nothing Then Throw New ArgumentNullException(NameOf(def))
            _def = def

            ' Compilar condiciones de senal
            _fnCall = CondicionCompilador.Compilar(def.SenalCall)
            _fnPut  = CondicionCompilador.Compilar(def.SenalPut)

            ' Compilar ajustes de confianza
            _ajustes = New List(Of ValueTuple(Of Func(Of EvalContext, Boolean), Integer))()
            If def.AjustesConfianza IsNot Nothing Then
                For Each aj In def.AjustesConfianza
                    If aj.Si IsNot Nothing Then
                        _ajustes.Add((CondicionCompilador.Compilar(aj.Si), aj.Sumar))
                    End If
                Next
            End If

            ' Pre-instanciar indicadores (evita overhead en cada llamada a Analizar)
            _indicadores = New List(Of IIndicator)()
            If def.Indicadores IsNot Nothing Then
                For Each indCfg In def.Indicadores
                    Try
                        _indicadores.Add(IndicadorFabrica.Crear(indCfg))
                    Catch ex As Exception
                        Throw New InvalidOperationException(
                            String.Format("EstrategiaJson '{0}': error creando indicador '{1}' (tipo '{2}'): {3}",
                                          def.Nombre, indCfg.Id, indCfg.Tipo, ex.Message), ex)
                    End Try
                Next
            End If
        End Sub

        ' ── IEstrategia ─────────────────────────────────────────────────
        Public ReadOnly Property Nombre As String Implements IEstrategia.Nombre
            Get
                Return _def.Nombre
            End Get
        End Property

        Public ReadOnly Property Descripcion As String Implements IEstrategia.Descripcion
            Get
                Return _def.Descripcion
            End Get
        End Property

        ''' <summary>
        ''' Evalua la estrategia sobre la ventana de velas indicada.
        ''' Las velas deben estar ordenadas oldest→newest.
        ''' </summary>
        Public Function Analizar(candles As IEnumerable(Of CandleModel)) As EstrategiaResultado Implements IEstrategia.Analizar
            Dim resultado As New EstrategiaResultado()

            Try
                Dim candleList = candles.ToList()
                If candleList.Count = 0 Then Return resultado

                Dim ctx = New EvalContext()

                ' ── 1. Poblar EvalContext con valores de cada indicador ─────
                PoblarContexto(ctx, candleList)

                ' ── 2. Agregar precios OHLC del ultimo candle ───────────────
                Dim ultimo = candleList.Last()
                ctx.SetValor("close", ultimo.ClosePrice)
                ctx.SetValor("open",  ultimo.OpenPrice)
                ctx.SetValor("high",  ultimo.High)
                ctx.SetValor("low",   ultimo.Low)
                AgregarCamposDeVela(ctx, candleList)

                ' ── 3. Evaluar condiciones de senal ────────────────────────
                Dim esCall As Boolean = False
                Dim esPut  As Boolean = False
                Try : esCall = _fnCall(ctx) : Catch : End Try
                Try : esPut  = _fnPut(ctx)  : Catch : End Try

                If Not esCall AndAlso Not esPut Then
                    resultado.Senal = "NEUTRAL"
                    resultado.Confianza = 0
                    PoblarDatos(resultado, ctx)
                    Return resultado
                End If

                resultado.Senal = If(esCall, "CALL", "PUT")

                ' ── 4. Calcular confianza ──────────────────────────────────
                Dim confianza As Integer = _def.ConfianzaBase
                For Each aj In _ajustes
                    Try
                        If aj.Item1(ctx) Then confianza += aj.Item2
                    Catch
                        ' ignorar ajuste fallido
                    End Try
                Next
                ' Limitar al rango 0-100
                resultado.Confianza = Math.Max(0, Math.Min(100, confianza))

                ' ── 5. Motivo legible ──────────────────────────────────────
                resultado.Motivo = String.Format("{0} | Confianza: {1} | Estrategia: {2}",
                                                  resultado.Senal, resultado.Confianza, _def.Nombre)

                ' ── 6. Volcar contexto en Datos ────────────────────────────
                PoblarDatos(resultado, ctx)

            Catch ex As Exception
                resultado.Senal    = "NEUTRAL"
                resultado.Confianza = 0
                resultado.Motivo   = "ERROR: " & ex.Message
            End Try

            Return resultado
        End Function

        ' ── Propiedad adicional para acceso a la definicion ─────────────
        ''' <summary>Definicion JSON origen de esta estrategia.</summary>
        Public ReadOnly Property Definicion As EstrategiaDinamicaDef
            Get
                Return _def
            End Get
        End Property

        ''' <summary>
        ''' Campos derivados de la vela (disponibles en cualquier estrategia):
        '''   hora_utc (0-23), dia_semana (0 = domingo), rango (high - low),
        '''   cuerpo (close - open, con signo), cuerpo_abs, y la vela anterior:
        '''   prev_open, prev_high, prev_low, prev_close, prev_cuerpo.
        ''' </summary>
        Friend Shared Sub AgregarCamposDeVela(ctx As EvalContext, candleList As List(Of CandleModel))
            Dim ultimo = candleList(candleList.Count - 1)
            Dim t = DateTimeOffset.FromUnixTimeSeconds(ultimo.Epoch).UtcDateTime
            ctx.SetValor("hora_utc", t.Hour)
            ctx.SetValor("dia_semana", CInt(t.DayOfWeek))
            ctx.SetValor("rango", ultimo.High - ultimo.Low)
            ctx.SetValor("cuerpo", ultimo.ClosePrice - ultimo.OpenPrice)
            ctx.SetValor("cuerpo_abs", Math.Abs(ultimo.ClosePrice - ultimo.OpenPrice))
            If candleList.Count >= 2 Then
                Dim prev = candleList(candleList.Count - 2)
                ctx.SetValor("prev_open", prev.OpenPrice)
                ctx.SetValor("prev_high", prev.High)
                ctx.SetValor("prev_low", prev.Low)
                ctx.SetValor("prev_close", prev.ClosePrice)
                ctx.SetValor("prev_cuerpo", prev.ClosePrice - prev.OpenPrice)
            End If
        End Sub

        ' ── Helpers privados ────────────────────────────────────────────
        Private Sub PoblarContexto(ctx As EvalContext, candleList As List(Of CandleModel))
            For i As Integer = 0 To _indicadores.Count - 1
                Dim indCfg = _def.Indicadores(i)
                Dim indicador = _indicadores(i)
                Try
                    Dim resultados = indicador.Calculate(candleList)
                    If resultados Is Nothing OrElse resultados.Count = 0 Then Continue For

                    Dim ultimo = resultados.Last()

                    ' Valor principal (siempre, si existe)
                    If ultimo.IndicatorValue.HasValue Then
                        ctx.SetValor(indCfg.Id, ultimo.IndicatorValue.Value)
                    End If

                    ' Valores adicionales: "id.SubCampo"
                    If ultimo.AdditionalValues IsNot Nothing Then
                        For Each kv In ultimo.AdditionalValues
                            If kv.Value.HasValue Then
                                ctx.SetValor(indCfg.Id & "." & kv.Key, kv.Value.Value)
                            End If
                        Next
                    End If
                Catch
                    ' Si un indicador falla (datos insuficientes, etc.) se omite silenciosamente
                End Try
            Next
        End Sub

        Private Shared Sub PoblarDatos(resultado As EstrategiaResultado, ctx As EvalContext)
            For Each clave In ctx.Claves
                Dim val As Decimal
                If ctx.TryGetValor(clave, val) Then
                    resultado.Datos(clave) = val
                End If
            Next
        End Sub

    End Class

End Namespace
