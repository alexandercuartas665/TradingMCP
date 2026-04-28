' ============================================================
' IndicadorFabrica.vb
'
' Fabrica que mapea un IndicadorConfig ({tipo, params}) al
' objeto IIndicator concreto correspondiente.
'
' Tipos soportados:
'   EMA, SMA, WMA, RSI, MACD, BollingerBands, ATR, DonchianChannel,
'   Supertrend, ADX, CCI, Momentum, Ichimoku, ParabolicSAR,
'   SSLChannel, QTrend, MFI, RVI, QQEMOD, AbsoluteStrengthHistogram,
'   ATRStopLoss, PivotPoints, FibonacciChannel, Alligator
'
' Namespace: WpfDerivClientVB.EstrategiasDinamicas
' ============================================================
Imports System
Imports WpfDerivClientVB.WpfDerivClientVB.Indicators

Namespace WpfDerivClientVB.EstrategiasDinamicas

    ''' <summary>
    ''' Crea instancias de IIndicator a partir de la configuracion JSON.
    ''' </summary>
    Public Module IndicadorFabrica

        ''' <summary>
        ''' Crea un indicador concreto segun el tipo y parametros indicados.
        ''' </summary>
        ''' <param name="config">Configuracion del indicador (tipo + params del JSON).</param>
        ''' <returns>Instancia de IIndicator lista para usar.</returns>
        ''' <exception cref="NotSupportedException">Si el tipo no esta soportado.</exception>
        Public Function Crear(config As IndicadorConfig) As IIndicator
            Dim tipo = If(config.Tipo, "").Trim().ToUpperInvariant()

            Select Case tipo
                ' ─── Medias moviles ───────────────────────────────────────
                Case "EMA"
                    Return New MovingAverages(MovingAverageType.EMA, GetInt(config, "periodo", 20))

                Case "SMA"
                    Return New MovingAverages(MovingAverageType.SMA, GetInt(config, "periodo", 20))

                Case "WMA"
                    Return New MovingAverages(MovingAverageType.WMA, GetInt(config, "periodo", 20))

                ' ─── Momentum ─────────────────────────────────────────────
                Case "RSI"
                    Return New RSI(GetInt(config, "periodo", 14))

                Case "CCI"
                    Return New CCI(GetInt(config, "periodo", 20))

                Case "MOMENTUM"
                    Return New Momentum(GetInt(config, "periodo", 10))

                Case "MFI"
                    Return New MFI(GetInt(config, "periodo", 14))

                Case "RVI"
                    Return New RVI(GetInt(config, "periodo", 10))

                Case "ABSOLUTESTRENGTHHISTOGRAM", "ASH"
                    Return New AbsoluteStrengthHistogram(GetInt(config, "periodo", 9))

                Case "QQEMOD"
                    Return New QQEMOD(GetInt(config, "periodo", 14), GetInt(config, "suavizado", 5))

                ' ─── Trend ────────────────────────────────────────────────
                Case "MACD"
                    Return New MACD(
                        GetInt(config, "rapido", 12),
                        GetInt(config, "lento", 26),
                        GetInt(config, "senal", 9))

                Case "DONCHIANCHANNEL", "DONCHIAN", "DC"
                    Return New DonchianChannel(GetInt(config, "periodo", 20))

                Case "SUPERTREND"
                    Return New Supertrend(
                        GetInt(config, "periodo", 10),
                        GetDec(config, "multiplicador", 3.0D))

                Case "ADX"
                    Return New ADX(GetInt(config, "periodo", 14))

                Case "ICHIMOKU"
                    Return New Ichimoku(
                        GetInt(config, "tenkan", 9),
                        GetInt(config, "kijun", 26),
                        GetInt(config, "senkou", 52))

                Case "SSLCHANNEL", "SSL"
                    Return New SSLChannel(GetInt(config, "periodo", 10))

                Case "PARABOLICSAR", "PSAR", "SAR"
                    Return New ParabolicSAR(
                        GetDec(config, "aceleracion", 0.02D),
                        GetDec(config, "max", 0.2D))

                Case "ALLIGATOR"
                    Return New Alligator()

                Case "QTREND"
                    Return New QTrend(GetInt(config, "periodo", 21))

                ' ─── Volatilidad ──────────────────────────────────────────
                Case "BOLLINGERBANDS", "BB", "BOLLINGER"
                    Return New BollingerBands(
                        GetInt(config, "periodo", 20),
                        GetDec(config, "desviacion", 2.0D))

                Case "ATR"
                    Return New ATR(GetInt(config, "periodo", 14))

                Case "ATRSTOPLOSS"
                    Return New ATRStopLoss(
                        GetInt(config, "periodo", 14),
                        GetDec(config, "multiplicador", 3.0D))

                ' ─── Soporte/Resistencia ──────────────────────────────────
                Case "PIVOTPOINTS", "PIVOT"
                    Return New PivotPoints(GetInt(config, "tipo", 1))

                Case "FIBONACCICHANNEL", "FIBONACCI", "FIB"
                    Return New FibonacciChannel(GetInt(config, "periodo", 100))

                Case Else
                    Throw New NotSupportedException(
                        String.Format("IndicadorFabrica: tipo '{0}' no soportado. Id: '{1}'.",
                                      config.Tipo, config.Id))
            End Select
        End Function

        ' ── Helpers de extraccion de parametros ─────────────────────────
        Private Function GetInt(config As IndicadorConfig, clave As String, defVal As Integer) As Integer
            If config.Params Is Nothing OrElse Not config.Params.ContainsKey(clave) Then Return defVal
            Try
                Return CInt(config.Params(clave))
            Catch
                Return defVal
            End Try
        End Function

        Private Function GetDec(config As IndicadorConfig, clave As String, defVal As Decimal) As Decimal
            If config.Params Is Nothing OrElse Not config.Params.ContainsKey(clave) Then Return defVal
            Try
                Return CDec(config.Params(clave))
            Catch
                Return defVal
            End Try
        End Function

    End Module

End Namespace
