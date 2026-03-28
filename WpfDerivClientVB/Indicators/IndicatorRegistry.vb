Imports System.Collections.Generic
Imports System.Linq

Namespace WpfDerivClientVB.Indicators

    ''' <summary>
    ''' Registro central de todos los indicadores técnicos disponibles en el sistema.
    '''
    ''' Punto de entrada principal para futura integración MCP:
    '''   - <see cref="GetAll"/> → lista completa de indicadores (Tool discovery)
    '''   - <see cref="GetByName"/> → buscar por nombre exacto o parcial
    '''   - <see cref="GetByCategory"/> → filtrar por categoría de análisis
    '''
    ''' Para agregar un nuevo indicador al registro, simplemente agregar una entrada
    ''' en el método <see cref="BuildRegistry"/> con los parámetros por defecto.
    '''
    ''' Cuando se integre como MCP, el servidor registrará cada indicador como una
    ''' Tool con nombre = IIndicator.Name y descripción = IIndicator.Description.
    ''' </summary>
    Public Module IndicatorRegistry

        ''' <summary>
        ''' Construye y retorna la lista completa de indicadores disponibles
        ''' con sus parámetros por defecto. Cada llamada crea nuevas instancias.
        ''' </summary>
        Public Function GetAll() As List(Of IIndicator)
            Return BuildRegistry()
        End Function

        ''' <summary>
        ''' Retorna el primer indicador cuyo <see cref="IIndicator.Name"/> contiene
        ''' el texto buscado (sin distinción de mayúsculas).
        ''' Retorna Nothing si no se encuentra.
        ''' </summary>
        ''' <param name="name">Nombre o fragmento: "RSI", "MACD", "SMA", "BB".</param>
        Public Function GetByName(name As String) As IIndicator
            Return BuildRegistry().FirstOrDefault(
                Function(ind) ind.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
        End Function

        ''' <summary>
        ''' Retorna todos los indicadores de una categoría específica.
        ''' Categorías disponibles: "Trend", "Momentum", "Volatility".
        ''' </summary>
        ''' <param name="category">Categoría exacta (case-insensitive).</param>
        Public Function GetByCategory(category As String) As List(Of IIndicator)
            Return BuildRegistry().Where(
                Function(ind) ind.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList()
        End Function

        ''' <summary>
        ''' Retorna el catálogo de indicadores formateado como texto,
        ''' útil para logging y futura exposición como recurso MCP.
        ''' </summary>
        Public Function GetCatalog() As String
            Dim sb As New System.Text.StringBuilder()
            sb.AppendLine("=== Catálogo de Indicadores Técnicos ===")
            sb.AppendLine()

            Dim categories As String() = {"Trend", "Momentum", "Volatility", "Support/Resistance"}
            For Each cat In categories
                Dim group = BuildRegistry().Where(Function(i) i.Category.Equals(cat, StringComparison.OrdinalIgnoreCase)).ToList()
                If group.Count = 0 Then Continue For
                sb.AppendLine(String.Format("── {0} ──────────────────────────────────", cat))
                For Each ind In group
                    sb.AppendLine(String.Format("  [{0}]", ind.Name))
                    sb.AppendLine(String.Format("    {0}", ind.Description))
                    sb.AppendLine()
                Next
            Next
            Return sb.ToString()
        End Function

        ' ================================================================
        '  REGISTRO INTERNO — Agregar aqui los nuevos indicadores
        ' ================================================================
        Private Function BuildRegistry() As List(Of IIndicator)
            Dim list As New List(Of IIndicator)()
            ' Trend: medias móviles y MACD
            list.Add(New MovingAverages(MovingAverageType.SMA, 20))
            list.Add(New MovingAverages(MovingAverageType.EMA, 20))
            list.Add(New MovingAverages(MovingAverageType.WMA, 20))
            list.Add(New MACD(12, 26, 9))
            list.Add(New DonchianChannel(20))
            list.Add(New Supertrend(10, 3D))
            list.Add(New ADX(14))
            list.Add(New Ichimoku(9, 26, 52))
            list.Add(New SSLChannel(10))
            list.Add(New ParabolicSAR(0.02D, 0.2D))
            list.Add(New Alligator())
            list.Add(New QTrend(21))

            ' Momentum
            list.Add(New RSI(14))
            list.Add(New CCI(20))
            list.Add(New MFI(14))
            list.Add(New Momentum(10))
            list.Add(New RVI(10))
            list.Add(New AbsoluteStrengthHistogram(9))
            list.Add(New QQEMOD(14, 5))

            ' Volatility
            list.Add(New BollingerBands(20, 2D))
            list.Add(New ATR(14))
            list.Add(New ATRStopLoss(14, 3D))

            ' Support/Resistance
            list.Add(New PivotPoints(1))
            list.Add(New FibonacciChannel(100))
            Return list
        End Function
    End Module

End Namespace
