using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using MySql.Data.MySqlClient;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using PuntoCV = OpenCvSharp.Point;
using TamanoCV = OpenCvSharp.Size;

namespace BandaCS
{
    public partial class Form1 : Form
    {
        #region FUNCIONAMIENTO PRINCIPAL - VISIÓN, CLASIFICACIÓN Y CONTEO

        // 1. Estado de la cámara y tareas.
        private CancellationTokenSource cancelacion;
        private Task tareaVideo = Task.CompletedTask;
        private bool capturando;
        private volatile bool contando = false;
        private bool hayImagenUtil;
        private int revisionConteo;
        private volatile int valorUmbral = 0;
        private volatile int umbralAplicado;
        private volatile bool mostrarMascara;
        private volatile int esperaFotogramaMs;
        private volatile int tiempoProcesoMs;
        private volatile int cuadrosPorSegundo;
        private string rutaCaptura;
        private volatile string errorCaptura;
        private string errorSalida;

        // 2. Parámetros de detección y contadores.
        // Área mínima del detector inicial, referida a una imagen de 960 px de ancho.
        // Al reducir la imagen se ajusta proporcionalmente, sin añadir controles.
        private const double AreaMinima = 650;
        private const int OlvidoMs = 800;
        private int conteoTornillos;
        private int conteoTuercas;
        private int conteoNoRegistrados;
        private int ultimoValor = -1;
        private readonly Stopwatch cronometro = new Stopwatch();
        private readonly string[] nombres =
            { "TORNILLO", "TUERCA", "NO REGISTRADO" };

        // Única salida para una futura conexión con el controlador de los botes.
        // 0 = tornillo; 1 = tuerca; 2 = pieza no registrada. Pendiente no genera salida.
        // El controlador debe encolar la orden y regresar sin bloquear la cámara.
        public event Action<int> PiezaClasificada;

        // Seguimiento mínimo: recordar el lado de la línea evita contar dos veces.
        // Dos imágenes consecutivas iguales descartan destellos de un solo fotograma.
        private class PiezaSeguida
        {
            public PuntoCV Centro;
            public double Area;
            public long UltimaVista;
            public bool Actualizada;
            public bool Contada;
            public bool Cruzo;
            public int Lado;
            public int Categoria = -1;
            public int Coincidencias;
        }

        public Form1()
        {
            // Toda la interfaz está en Form1.Designer.cs.
            InitializeComponent();
            ConfigurarPanelBD();
        }

        // 3. La cámara permanece activa; este botón solo pausa el conteo.
        private void btnIniciar_Click(object sender, EventArgs e)
        {
            if (cerrando || !bdLista) return;
            if (!PuertoDisponible())
            {
                lblEstado.Text = "Arduino no conectado. Comprueba USB/COM.";
                _ = ConectarArduinoAsync();
                return;
            }
            EnviarArduino(estadoArduino == "RUNNING" ? 's' : 'r');
        }

        private async void btnReiniciar_Click(object sender, EventArgs e)
        {
            if (contando || cerrando || !bdLista || estadoArduino == "RUNNING") return;
            btnReiniciar.Enabled = false;
            btnIniciar.Enabled = false;
            // Cierra la sesión anterior y crea otra sin borrar el historial de ESTA apertura.
            await EncolarBD(() =>
            {
                CerrarSesionBD();
                CrearSesionBD();
            });
            if (!bdLista || cerrando) return;
            Interlocked.Increment(ref revisionConteo);
            conteoTornillos = 0;
            conteoTuercas = 0;
            conteoNoRegistrados = 0;
            ultimoValor = -1;
            cronometro.Reset();
            ActualizarEstadisticas();
            btnIniciar.Enabled = true;
            btnReiniciar.Enabled = true;
            lblEstado.Text = "Sesión nueva. Conteo reiniciado; cámara activa.";
            lblBDConexion.Text = "MySQL: conectado | Sesión " + idSesionActual;
            _ = RefrescarRegistrosBDAsync();
        }

        private void btnApagar_Click(object sender, EventArgs e)
        {
            apagadoSolicitado = true;
            Close();
        }

        private void trackUmbral_Scroll(object sender, EventArgs e)
        {
            valorUmbral = trackUmbral.Value;
            lblUmbral.Text = valorUmbral == 0
                ? "Umbral: AUTO (0)" : "Umbral manual: " + valorUmbral;
            Interlocked.Increment(ref revisionConteo);
        }

        private void chkMostrarMascara_CheckedChanged(object sender, EventArgs e)
        {
            mostrarMascara = chkMostrarMascara.Checked;
            splitVideos.Panel2Collapsed = !mostrarMascara;
            if (mostrarMascara)
                splitVideos.SplitterDistance = Math.Max(splitVideos.Panel1MinSize,
                    Math.Min((int)(splitVideos.ClientSize.Width * 0.70),
                    splitVideos.ClientSize.Width - splitVideos.SplitterWidth - splitVideos.Panel2MinSize));
        }

        // Exportar datos para revisar el detector, sin los cuadros ni textos de la interfaz.
        private void btnGuardarPrueba_Click(object sender, EventArgs e)
        {
            if (!hayImagenUtil || cerrando) return;
            using (SaveFileDialog dialogo = new SaveFileDialog())
            {
                dialogo.Filter = "Imagen PNG|*.png";
                dialogo.FileName = "prueba_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png";
                if (dialogo.ShowDialog(this) == DialogResult.OK)
                {
                    errorCaptura = null;
                    Interlocked.Exchange(ref rutaCaptura, dialogo.FileName);
                }
            }
        }

        private void GuardarPrueba(Mat frame, Mat mascara)
        {
            string ruta = Interlocked.Exchange(ref rutaCaptura, null);
            if (ruta == null) return;
            try
            {
                if (!Cv2.ImWrite(ruta, frame)) throw new IOException("No se pudo guardar el original.");
                string rutaMascara = Path.Combine(Path.GetDirectoryName(ruta),
                    Path.GetFileNameWithoutExtension(ruta) + "_mascara.png");
                if (!Cv2.ImWrite(rutaMascara, mascara)) throw new IOException("No se pudo guardar la máscara.");
            }
            catch (Exception ex) { errorCaptura = "No se pudo guardar la prueba: " + ex.Message; }
        }

        // 4. Detector basado en el inicial: gris, umbral, limpieza y contornos.
        private void ProcesarVideo(VideoCapture captura, CancellationToken token)
        {
            List<PiezaSeguida> piezas = new List<PiezaSeguida>();
            int revisionAnterior = -1;
            Stopwatch reloj = Stopwatch.StartNew();
            long inicioMedicion = 0;
            long ultimaPresentacion = -100;
            int cuadrosMedidos = 0;

            using (FotogramaReciente fuente = new FotogramaReciente(captura, token))
            using (Mat frame = new Mat())
            using (Mat gris = new Mat())
            using (Mat suavizada = new Mat())
            using (Mat mascara = new Mat())
            using (Mat limpiar = Cv2.GetStructuringElement(
                MorphShapes.Ellipse, new TamanoCV(3, 3)))
            {
                while (!token.IsCancellationRequested)
                {
                    int espera;
                    if (!fuente.Tomar(frame, out espera)) break;
                    esperaFotogramaMs = espera;
                    long ahora = reloj.ElapsedMilliseconds;
                    int revision = Volatile.Read(ref revisionConteo);
                    if (revision != revisionAnterior)
                    {
                        foreach (PiezaSeguida pieza in piezas)
                        {
                            pieza.Lado = 0;
                            pieza.Cruzo = false;
                            pieza.Coincidencias = 0;
                        }
                        revisionAnterior = revision;
                    }
                    if (frame.Width > 960)
                        Cv2.Resize(frame, frame, new TamanoCV(960,
                            (int)(frame.Height * 960.0 / frame.Width)), 0, 0, InterpolationFlags.Area);
                    Cv2.CvtColor(frame, gris, ColorConversionCodes.BGR2GRAY);
                    bool imagenOscura = Cv2.Mean(gris).Val0 < 3;
                    Cv2.GaussianBlur(gris, suavizada, new TamanoCV(3, 3), 0);
                    Rect zona = ObtenerZona(frame);
                    CrearMascara(suavizada, mascara, zona, limpiar);
                    GuardarPrueba(frame, mascara);
                    int lineaX = zona.Left + zona.Width / 2;
                    Cv2.Line(frame, new PuntoCV(lineaX, zona.Top),
                        new PuntoCV(lineaX, zona.Bottom), Scalar.Yellow, 2);
                    foreach (PiezaSeguida pieza in piezas) pieza.Actualizada = false;
                    piezas.RemoveAll(p => ahora - p.UltimaVista > OlvidoMs);
                    List<int> conteos = new List<int>();
                    double escala = frame.Width / 960.0;

                    // ApproxSimple recupera el contorno sin guardar cada píxel del borde.
                    Cv2.FindContours(mascara, out PuntoCV[][] contornos,
                        out HierarchyIndex[] jerarquia, RetrievalModes.CComp,
                        ContourApproximationModes.ApproxSimple);
                    for (int i = 0; i < contornos.Length && !imagenOscura; i++)
                    {
                        if (jerarquia[i].Parent != -1) continue;
                        double area = Cv2.ContourArea(contornos[i]);
                        if (area < AreaMinima * escala * escala
                            || area > zona.Width * (double)zona.Height * 0.20) continue;
                        Rect caja = Cv2.BoundingRect(contornos[i]);
                        if (TocaBorde(caja, zona)) continue;
                        RotatedRect forma = Cv2.MinAreaRect(contornos[i]);
                        double ancho = Math.Min(forma.Size.Width, forma.Size.Height);
                        double largo = Math.Max(forma.Size.Width, forma.Size.Height);
                        // Puntos, hilos y reflejos muy finos no son objetos para los botes.
                        if (ancho < 6 * escala || area / Math.Max(1, ancho * largo) < 0.20)
                            continue;
                        int categoria = ClasificarObjeto(contornos, jerarquia, i, forma, area);
                        PuntoCV centro = new PuntoCV((int)forma.Center.X, (int)forma.Center.Y);
                        PiezaSeguida pieza = BuscarPieza(piezas, centro, area, frame.Width);
                        if (pieza == null)
                        {
                            pieza = new PiezaSeguida();
                            piezas.Add(pieza);
                        }
                        pieza.Centro = centro;
                        pieza.Area = area;
                        pieza.UltimaVista = ahora;
                        pieza.Actualizada = true;
                        pieza.Coincidencias = pieza.Categoria == categoria
                            ? Math.Min(2, pieza.Coincidencias + 1) : 1;
                        pieza.Categoria = categoria;
                        int lado = centro.X < lineaX ? -1 : 1;
                        if (pieza.Lado != 0 && lado != pieza.Lado) pieza.Cruzo = true;
                        pieza.Lado = lado;
                        if (!pieza.Contada && pieza.Cruzo && pieza.Coincidencias >= 2)
                        {
                            pieza.Contada = true;
                            if (contando) conteos.Add(categoria);
                        }
                        if (pieza.Coincidencias >= 2)
                            DibujarPieza(frame, forma, caja, categoria);
                    }
                    // Si una pieza desaparece, no acumular confirmaciones discontinuas.
                    foreach (PiezaSeguida pieza in piezas)
                        if (!pieza.Actualizada) pieza.Coincidencias = 0;
                    if (token.IsCancellationRequested) break;
                    tiempoProcesoMs = (int)(reloj.ElapsedMilliseconds - ahora);
                    cuadrosMedidos++;
                    if (ahora - inicioMedicion >= 1000)
                    {
                        cuadrosPorSegundo = (int)(cuadrosMedidos * 1000.0 / (ahora - inicioMedicion));
                        cuadrosMedidos = 0;
                        inicioMedicion = ahora;
                    }
                    if (ahora - ultimaPresentacion >= 50 || conteos.Count > 0)
                    {
                        MostrarResultado(frame, mascara, conteos, revision, imagenOscura);
                        ultimaPresentacion = ahora;
                    }
                }
            }
        }

        private Rect ObtenerZona(Mat frame)
        {
            int horizontal = (int)(frame.Width * 0.06);
            int vertical = (int)(frame.Height * 0.06);
            return new Rect(horizontal, vertical,
                frame.Width - horizontal * 2, frame.Height - vertical * 2);
        }

        private void CrearMascara(Mat suavizada, Mat mascara, Rect zona, Mat limpiar)
        {
            using (Mat region = suavizada[zona])
            using (Mat binaria = new Mat())
            {
                // 0 = Otsu automático. Los demás valores conservan el ajuste manual inicial.
                int ajuste = valorUmbral;
                umbralAplicado = (int)Math.Round(Cv2.Threshold(region, binaria, ajuste, 255,
                    ajuste == 0 ? ThresholdTypes.Binary | ThresholdTypes.Otsu : ThresholdTypes.BinaryInv));
                // Las piezas deben ser blancas tanto sobre banda clara como sobre banda oscura.
                // Supone que el fondo ocupa la mayor parte de la zona de inspección.
                if (Cv2.CountNonZero(binaria) > region.Width * (double)region.Height * 0.5)
                    Cv2.BitwiseNot(binaria, binaria);
                // Cerrar huecos pequeños antes de quitar ruido evita romper aros delgados.
                Cv2.MorphologyEx(binaria, binaria, MorphTypes.Close, limpiar);
                Cv2.MorphologyEx(binaria, binaria, MorphTypes.Open, limpiar);
                // El cierre de 15x15 del inicial podía rellenar el agujero de una tuerca.
                mascara.Create(suavizada.Size(), MatType.CV_8UC1);
                mascara.SetTo(Scalar.Black);
                using (Mat destino = mascara[zona]) binaria.CopyTo(destino);
            }
        }

        private bool TocaBorde(Rect caja, Rect zona)
        {
            return caja.Left <= zona.Left + 6 || caja.Right >= zona.Right - 6
                || caja.Top <= zona.Top + 6 || caja.Bottom >= zona.Bottom - 6;
        }

        // 5. Solo tres resultados. Las mediciones no se muestran sobre la cámara.
        private int ClasificarObjeto(PuntoCV[][] contornos, HierarchyIndex[] jerarquia,
            int indice, RotatedRect forma, double area)
        {
            PuntoCV[] contorno = contornos[indice];
            double ancho = Math.Min(forma.Size.Width, forma.Size.Height);
            double largo = Math.Max(forma.Size.Width, forma.Size.Height);
            if (ancho <= 0) return 2;
            double alargamiento = largo / ancho;
            double ocupacion = area / (ancho * largo);
            bool agujero = false;
            double perimetro = Cv2.ArcLength(contorno, true);
            double circularidad = perimetro > 0 ? 4 * Math.PI * area / (perimetro * perimetro) : 0;
            for (int hijo = jerarquia[indice].Child; hijo != -1; hijo = jerarquia[hijo].Next)
            {
                double proporcion = Cv2.ContourArea(contornos[hijo]) / area;
                if (proporcion < 0.04 || proporcion > 0.60) continue;
                Moments m = Cv2.Moments(contornos[hijo]);
                if (Math.Abs(m.M00) < 0.001) continue;
                double dx = m.M10 / m.M00 - forma.Center.X;
                double dy = m.M01 / m.M00 - forma.Center.Y;
                double bordeAgujero = Cv2.ArcLength(contornos[hijo], true);
                double redondez = bordeAgujero > 0
                    ? 4 * Math.PI * Cv2.ContourArea(contornos[hijo]) / (bordeAgujero * bordeAgujero) : 0;
                if (dx * dx + dy * dy < ancho * ancho * 0.09 && redondez >= 0.45)
                    agujero = true;
            }
            if (alargamiento < 1.55 && agujero && circularidad >= 0.45)
            {
                PuntoCV[] envolvente = Cv2.ConvexHull(contorno);
                PuntoCV[] poligono = Cv2.ApproxPolyDP(envolvente,
                    Cv2.ArcLength(envolvente, true) * 0.03, true);
                // Exigir caras exteriores: una moneda o una arandela no es una tuerca.
                double solidez = area / Math.Max(1, Cv2.ContourArea(envolvente));
                if (poligono.Length >= 5 && poligono.Length <= 7
                    && ocupacion >= 0.55 && solidez >= 0.85)
                    return 1;
            }
            if (alargamiento >= 1.60 && alargamiento <= 14 && !agujero
                && ocupacion >= 0.20 && ocupacion < 0.90 && TieneCabeza(contorno, forma))
                return 0;
            return 2;
        }

        // Compara diez cortes: busca la cabeza en los extremos y un cuerpo uniforme.
        // Interpolar los bordes permite usar contornos simplificados, incluso al girar la pieza.
        private bool TieneCabeza(PuntoCV[] contorno, RotatedRect forma)
        {
            double largo = Math.Max(forma.Size.Width, forma.Size.Height);
            double angulo = (forma.Angle + (forma.Size.Width < forma.Size.Height ? 90 : 0))
                * Math.PI / 180;
            double coseno = Math.Cos(angulo), seno = Math.Sin(angulo);
            double[] anchos = new double[10];
            for (int corte = 0; corte < anchos.Length; corte++)
            {
                double posicion = largo * (-0.45 + corte * 0.10);
                double minimo = double.PositiveInfinity, maximo = double.NegativeInfinity;
                for (int i = 0; i < contorno.Length; i++)
                {
                    PuntoCV a = contorno[i], b = contorno[(i + 1) % contorno.Length];
                    double ax = (a.X - forma.Center.X) * coseno + (a.Y - forma.Center.Y) * seno;
                    double bx = (b.X - forma.Center.X) * coseno + (b.Y - forma.Center.Y) * seno;
                    if ((ax <= posicion && bx > posicion) || (bx <= posicion && ax > posicion))
                    {
                        double ay = -(a.X - forma.Center.X) * seno + (a.Y - forma.Center.Y) * coseno;
                        double by = -(b.X - forma.Center.X) * seno + (b.Y - forma.Center.Y) * coseno;
                        double y = ay + (by - ay) * (posicion - ax) / (bx - ax);
                        minimo = Math.Min(minimo, y);
                        maximo = Math.Max(maximo, y);
                    }
                }
                if (double.IsInfinity(minimo)) return false;
                anchos[corte] = maximo - minimo;
            }
            double cuerpo = 0, variacion = 0;
            for (int i = 2; i < 8; i++) cuerpo += anchos[i] / 6;
            if (cuerpo <= 0) return false;
            for (int i = 2; i < 8; i++) variacion += Math.Pow(anchos[i] - cuerpo, 2) / 6;
            double extremoA = (anchos[0] + anchos[1]) / 2;
            double extremoB = (anchos[8] + anchos[9]) / 2;
            double cabeza = Math.Max(extremoA, extremoB) / cuerpo;
            return cabeza >= 1.30 && cabeza <= 4.50
                && Math.Min(extremoA, extremoB) <= cuerpo * 1.30
                && Math.Sqrt(variacion) / cuerpo <= 0.45;
        }

        // 6. Asociación por proximidad y tamaño; sin votaciones ni espera de un segundo.
        private PiezaSeguida BuscarPieza(List<PiezaSeguida> piezas, PuntoCV centro,
            double area, int anchoImagen)
        {
            PiezaSeguida mejor = null;
            double distanciaMinima = anchoImagen * anchoImagen * 0.01;
            foreach (PiezaSeguida pieza in piezas)
            {
                if (pieza.Actualizada || area < pieza.Area * 0.40 || area > pieza.Area * 2.50)
                    continue;
                double dx = centro.X - pieza.Centro.X, dy = centro.Y - pieza.Centro.Y;
                double distancia = dx * dx + dy * dy;
                if (distancia < distanciaMinima)
                {
                    distanciaMinima = distancia;
                    mejor = pieza;
                }
            }
            return mejor;
        }

        private void DibujarPieza(Mat frame, RotatedRect forma, Rect caja, int categoria)
        {
            Scalar color = categoria == 0 ? Scalar.LimeGreen
                : categoria == 1 ? Scalar.DeepSkyBlue : Scalar.Red;
            Point2f[] vertices = forma.Points();
            for (int i = 0; i < 4; i++)
                Cv2.Line(frame, (PuntoCV)vertices[i], (PuntoCV)vertices[(i + 1) % 4], color, 2);
            Cv2.PutText(frame, nombres[categoria],
                new PuntoCV(caja.X, Math.Max(caja.Y - 10, 22)),
                HersheyFonts.HersheySimplex, 0.55, color, 2);
        }

        // 7. Entrega de imágenes y resultados a la ventana.
        private void MostrarResultado(Mat frame, Mat mascara, List<int> conteos,
            int revision, bool imagenOscura)
        {
            Bitmap color = BitmapConverter.ToBitmap(frame);
            Bitmap binaria = null;
            try
            {
                if (mostrarMascara) binaria = BitmapConverter.ToBitmap(mascara);
                Invoke(new Action(() =>
                {
                    picOriginal.Image?.Dispose();
                    pictMascara.Image?.Dispose();
                    picOriginal.Image = color;
                    pictMascara.Image = binaria;
                    lblUmbral.Text = valorUmbral == 0
                        ? "Umbral AUTO: " + umbralAplicado + " (0)"
                        : "Umbral manual: " + valorUmbral;
                    hayImagenUtil = !imagenOscura;
                    if (contando && revision == Volatile.Read(ref revisionConteo))
                    {
                        foreach (int categoria in conteos)
                        {
                            ultimoValor = categoria;
                            if (categoria == 0) conteoTornillos++;
                            else if (categoria == 1) conteoTuercas++;
                            else conteoNoRegistrados++;
                            // Incluye la categoría 2 para su bote correspondiente.
                            // No hay conexión serial ni movimientos de servos en este archivo.
                            try
                            {
                                // Base de datos y Arduino trabajan fuera del hilo de video.
                                if (bdLista) _ = EncolarBD(() => GuardarClasificacionBD(categoria));
                                EncolarClasificacionArduino(categoria);
                                PiezaClasificada?.Invoke(categoria);
                            }
                            catch (Exception ex)
                            {
                                // No reintentar una orden: podría repetir un movimiento físico.
                                errorSalida = "Salida del controlador: " + ex.Message;
                                contando = false;
                                Interlocked.Increment(ref revisionConteo);
                                btnIniciar.Text = "Reanudar conteo";
                                btnIniciar.BackColor = Color.LightGreen;
                                btnReiniciar.Enabled = true;
                                break;
                            }
                        }
                    }
                    if (contando && hayImagenUtil && !cerrando) cronometro.Start();
                    else cronometro.Stop();
                    ActualizarEstadisticas();
                    if (!cerrando)
                    {
                        if (!string.IsNullOrEmpty(errorSalida))
                            lblEstado.Text = errorSalida;
                        else if (!string.IsNullOrEmpty(errorCaptura))
                            lblEstado.Text = errorCaptura;
                        else if (estadoArduino == "EMERGENCY")
                            lblEstado.Text = "EMERGENCIA FÍSICA. Banda detenida; cámara activa.";
                        else if (!bdLista && !string.IsNullOrEmpty(errorBD))
                            lblEstado.Text = errorBD;
                        else if (estadoArduino == "LINK_LOST")
                            lblEstado.Text = "Arduino desconectado. Banda detenida; cámara activa.";
                        else if (imagenOscura)
                            lblEstado.Text = string.IsNullOrEmpty(errorServicios)
                                ? "Imagen casi negra. Revisa la fuente de OBS y su cámara virtual."
                                : errorServicios;
                        else
                            lblEstado.Text = contando
                                ? "Cámara activa. Una clasificación al cruzar la línea amarilla."
                                : "Conteo pausado. La cámara sigue transmitiendo.";
                    }
                }));
                // Ahora los PictureBox conservan estas imágenes.
                color = null;
                binaria = null;
            }
            finally
            {
                color?.Dispose();
                binaria?.Dispose();
            }
        }

        private void ActualizarEstadisticas()
        {
            int total = conteoTornillos + conteoTuercas + conteoNoRegistrados;
            int registradas = conteoTornillos + conteoTuercas;
            double minutos = cronometro.Elapsed.TotalMinutes;
            double promedio = minutos > 0 ? total / minutos : 0;
            double porcentaje = total > 0 ? registradas * 100.0 / total : 0;
            string ultima = ultimoValor < 0 ? "Ninguna"
                : nombres[ultimoValor] + " (valor " + ultimoValor + ")";
            lblConteoPiezas.Text = "TORNILLOS [0]: " + conteoTornillos +
                "     |     TUERCAS [1]: " + conteoTuercas +
                "     |     NO REGISTRADOS [2]: " + conteoNoRegistrados +
                Environment.NewLine + "Última pieza contada: " + ultima;
            lblIndicadores.Text = "Total: " + total + "     |     Promedio: " +
                promedio.ToString("F1") + " piezas/min     |     Registradas: " +
                porcentaje.ToString("F1") + "%" + Environment.NewLine +
                "Tiempo: " + cronometro.Elapsed.ToString(@"hh\:mm\:ss")
                + "  |  Análisis: " + tiempoProcesoMs + " ms  |  " + cuadrosPorSegundo + " FPS"
                + "  |  Espera de cuadro: " + esperaFotogramaMs + " ms";
        }

        #endregion

        // =========================================================================================
        // =========================================================================================
        #region BASE DE DATOS MYSQL (SESIONES Y CLASIFICACIONES)

        // EDITAR SOLO LOS DATOS DE CONEXIÓN. No requiere crear las tablas desde C#.
        // Recomendado: configurar variable de entorno SEPARADOR_MYSQL_PASSWORD.
        private readonly string conexionBD = new MySqlConnectionStringBuilder
        {
            Server = "localhost",
            Port = 3306,
            Database = "separador_industrial",
            UserID = "root",
            Password = "12345678",
            CharacterSet = "utf8mb4",
            ConnectionTimeout = 5
        }.ConnectionString;

        private readonly object candadoBD = new object();
        private Task colaBD = Task.CompletedTask;
        private volatile bool bdLista;
        private string errorBD;
        private int idSesionActual;
        private readonly Dictionary<int, int> idsCategoriaBD = new Dictionary<int, int>();

        // Solo se ejecuta una vez al abrir. Se borran sesiones e inspecciones,
        // pero se conservan las tres categorías y la estructura de MySQL.
        private async Task InicializarBDAsync()
        {
            btnIniciar.Enabled = false;
            btnReiniciar.Enabled = false;
            lblEstado.Text = "Preparando base de datos local...";
            try
            {
                await Task.Run(() =>
                {
                    using (var conexion = new MySqlConnection(conexionBD))
                    {
                        conexion.Open();
                        using (var tx = conexion.BeginTransaction())
                        {
                            using (var cmd = new MySqlCommand("DELETE FROM REGISTRO_INSPECCION", conexion, tx))
                                cmd.ExecuteNonQuery();
                            using (var cmd = new MySqlCommand("DELETE FROM SESION", conexion, tx))
                                cmd.ExecuteNonQuery();
                            tx.Commit();
                        }
                        // No suponemos que los ID de las categorías sean 1, 2 y 3.
                        using (var cmd = new MySqlCommand("SELECT id_categoria, nombre FROM CATEGORIA", conexion))
                        using (var lector = cmd.ExecuteReader())
                        {
                            idsCategoriaBD.Clear();
                            while (lector.Read())
                            {
                                string nombre = lector.GetString(1).Trim().ToUpperInvariant();
                                int clave = nombre == "TORNILLO" ? 0 : nombre == "TUERCA" ? 1
                                    : nombre == "NO REGISTRADO" ? 2 : -1;
                                if (clave >= 0) idsCategoriaBD[clave] = lector.GetInt32(0);
                            }
                        }
                        if (idsCategoriaBD.Count != 3)
                            throw new InvalidOperationException("Falta alguna categoría: Tuerca, Tornillo o No registrado.");
                    }
                    CrearSesionBD();
                });
                bdLista = true;
                errorBD = null;
                lblBDConexion.Text = "MySQL: conectado | Sesión " + idSesionActual;
                _ = RefrescarRegistrosBDAsync();
            }
            catch (Exception ex)
            {
                bdLista = false;
                contando = false;
                Interlocked.Increment(ref revisionConteo);
                errorBD = "MySQL no disponible: " + ex.Message;
                lblEstado.Text = errorBD;
                lblBDConexion.Text = "MySQL: error de conexión";
                // Nunca contar piezas sin haber abierto una sesión válida.
            }
        }

        private void CrearSesionBD()
        {
            using (var conexion = new MySqlConnection(conexionBD))
            {
                conexion.Open();
                using (var cmd = new MySqlCommand(
                    "INSERT INTO SESION (fecha_inicio) VALUES (NOW())", conexion))
                {
                    cmd.ExecuteNonQuery();
                    idSesionActual = Convert.ToInt32(cmd.LastInsertedId);
                }
            }
        }

        private void CerrarSesionBD()
        {
            if (idSesionActual <= 0) return;
            using (var conexion = new MySqlConnection(conexionBD))
            {
                conexion.Open();
                using (var cmd = new MySqlCommand(
                    "UPDATE SESION SET fecha_fin = NOW() WHERE id_sesion = @id AND fecha_fin IS NULL", conexion))
                {
                    cmd.Parameters.AddWithValue("@id", idSesionActual);
                    cmd.ExecuteNonQuery();
                }
            }
            idSesionActual = 0;
        }

        private void GuardarClasificacionBD(int categoria)
        {
            if (idSesionActual <= 0 || !idsCategoriaBD.ContainsKey(categoria))
                throw new InvalidOperationException("No hay sesión o categoría válida para registrar.");
            using (var conexion = new MySqlConnection(conexionBD))
            {
                conexion.Open();
                using (var cmd = new MySqlCommand(
                    "INSERT INTO REGISTRO_INSPECCION " +
                    "(id_sesion, id_categoria, fecha_hora, estado) " +
                    "VALUES (@sesion, @categoria, NOW(), 'Clasificada')", conexion))
                {
                    cmd.Parameters.AddWithValue("@sesion", idSesionActual);
                    cmd.Parameters.AddWithValue("@categoria", idsCategoriaBD[categoria]);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Cola secuencial: respeta el orden de las piezas y evita bloquear video/UI.
        private Task EncolarBD(Action operacion)
        {
            lock (candadoBD)
            {
                colaBD = colaBD.ContinueWith(_ =>
                {
                    if (!bdLista) return;
                    try { operacion(); }
                    catch (Exception ex)
                    {
                        bdLista = false;
                        EnviarArduino('s');
                        errorBD = "Error de MySQL: " + ex.Message;
                        if (!IsDisposed && IsHandleCreated)
                        {
                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    contando = false;
                                    Interlocked.Increment(ref revisionConteo);
                                    cronometro.Stop();
                                    btnIniciar.Enabled = false;
                                    btnReiniciar.Enabled = false;
                                    lblEstado.Text = errorBD;
                                }));
                            }
                            catch (InvalidOperationException) { }
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return colaBD;
            }
        }

        #endregion

        // =========================================================================================
        #region PANEL DE BASE DE DATOS EN TIEMPO REAL (CONSULTA CADA 2 SEGUNDOS)
        // El panel utiliza controles declarados en Form1.Designer.cs.
        private readonly Stopwatch relojConsultaBD = Stopwatch.StartNew();
        private bool consultaBDEnCurso;
        private bool panelBDConfigurado;

        private void ConfigurarPanelBD()
        {
            if (panelBDConfigurado) return;
            panelBDConfigurado = true;
            dgvInspecciones.ReadOnly = true;
            dgvInspecciones.AllowUserToAddRows = false;
            dgvInspecciones.AllowUserToDeleteRows = false;
            dgvInspecciones.MultiSelect = false;
            dgvInspecciones.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            dgvInspecciones.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            dgvInspecciones.ScrollBars = ScrollBars.Both;
            dgvInspecciones.RowHeadersVisible = false;
            lblBDConexion.Text = "MySQL: preparando conexión...";
            lblBDResumen.Text = "Sesión: — | Total: 0 | Actualización: cada 2 s";
        }

        private async Task ActualizarPanelBDSiCorrespondeAsync()
        {
            if (cerrando || !bdLista || consultaBDEnCurso || relojConsultaBD.ElapsedMilliseconds < 2000)
                return;
            relojConsultaBD.Restart();
            await RefrescarRegistrosBDAsync();
        }

        private async Task RefrescarRegistrosBDAsync()
        {
            if (consultaBDEnCurso || !bdLista || cerrando || IsDisposed) return;
            consultaBDEnCurso = true;
            try
            {
                // La lectura espera los INSERT pendientes para mostrar datos consistentes.
                Task pendientes;
                lock (candadoBD) pendientes = colaBD;
                await pendientes;
                if (!bdLista || cerrando) return;
                int sesion = idSesionActual;
                var resultado = await Task.Run(() =>
                {
                    var datos = new System.Data.DataTable();
                    int total = 0, tornillos = 0, tuercas = 0, otros = 0;
                    using (var conexion = new MySqlConnection(conexionBD))
                    {
                        conexion.Open();
                        using (var cmd = new MySqlCommand(
                            "SELECT r.id_registro AS 'Registro', " +
                            "DATE_FORMAT(r.fecha_hora, '%H:%i:%s') AS 'Hora', " +
                            "COALESCE(c.nombre, 'Pendiente') AS 'Categoría', " +
                            "r.estado AS 'Estado', COALESCE(r.observaciones, '') AS 'Observaciones' " +
                            "FROM REGISTRO_INSPECCION r LEFT JOIN CATEGORIA c " +
                            "ON r.id_categoria = c.id_categoria " +
                            "WHERE r.id_sesion = @sesion ORDER BY r.id_registro DESC LIMIT 100", conexion))
                        {
                            cmd.Parameters.AddWithValue("@sesion", sesion);
                            using (var da = new MySqlDataAdapter(cmd)) da.Fill(datos);
                        }
                        using (var cmd = new MySqlCommand(
                            "SELECT COUNT(*), " +
                            "COALESCE(SUM(c.nombre = 'Tornillo'), 0), " +
                            "COALESCE(SUM(c.nombre = 'Tuerca'), 0), " +
                            "COALESCE(SUM(c.nombre = 'No registrado'), 0) " +
                            "FROM REGISTRO_INSPECCION r LEFT JOIN CATEGORIA c " +
                            "ON r.id_categoria = c.id_categoria WHERE r.id_sesion = @sesion", conexion))
                        {
                            cmd.Parameters.AddWithValue("@sesion", sesion);
                            using (var lector = cmd.ExecuteReader())
                                if (lector.Read())
                                {
                                    total = Convert.ToInt32(lector.GetValue(0));
                                    tornillos = Convert.ToInt32(lector.GetValue(1));
                                    tuercas = Convert.ToInt32(lector.GetValue(2));
                                    otros = Convert.ToInt32(lector.GetValue(3));
                                }
                        }
                    }
                    return Tuple.Create(datos, total, tornillos, tuercas, otros);
                });
                if (cerrando || IsDisposed || sesion != idSesionActual) return;
                dgvInspecciones.DataSource = resultado.Item1;
                // Anchos fijos: conservar todas las columnas y habilitar desplazamiento horizontal.
                // Se aplica después del DataSource porque las columnas se generan de forma automática.
                if (dgvInspecciones.Columns.Contains("Registro")) dgvInspecciones.Columns["Registro"].Width = 85;
                if (dgvInspecciones.Columns.Contains("Hora")) dgvInspecciones.Columns["Hora"].Width = 85;
                if (dgvInspecciones.Columns.Contains("Categoría")) dgvInspecciones.Columns["Categoría"].Width = 120;
                if (dgvInspecciones.Columns.Contains("Estado")) dgvInspecciones.Columns["Estado"].Width = 105;
                if (dgvInspecciones.Columns.Contains("Observaciones"))
                {
                    dgvInspecciones.Columns["Observaciones"].Width = 300;
                    dgvInspecciones.Columns["Observaciones"].ToolTipText = "Desplázate horizontalmente para ver las observaciones completas";
                }
                dgvInspecciones.ClearSelection();
                lblBDConexion.Text = "MySQL: conectado | Sesión " + sesion + " | " + DateTime.Now.ToString("HH:mm:ss");
                lblBDResumen.Text = "Total: " + resultado.Item2 + " | Tornillos: " + resultado.Item3 +
                    " | Tuercas: " + resultado.Item4 + " | No registrados: " + resultado.Item5 +
                    " | Actualización: 2 s";
            }
            catch (Exception ex)
            {
                if (!cerrando && !IsDisposed)
                    lblBDConexion.Text = "Error de consulta MySQL: " + ex.Message;
            }
            finally { consultaBDEnCurso = false; }
        }
        #endregion

        // =========================================================================================
        #region CONEXIÓN ARDUINO MEGA - USB SERIAL 9600
        private SerialPort puertoArduino;
        private readonly object candadoSerial = new object();
        private volatile string estadoArduino = "SIN_CONEXION";
        private bool conectandoArduino;
        private bool servoAOcupado, servoBOcupado;
        private long ultimaRespuestaArduino;
        private long ultimoPingArduino;
        private long ultimoEnvioA, ultimoEnvioB;
        // Si no pudo despacharse antes de llegar al servo, se marca aviso.
        private const int LimiteEsperaOrdenMs = 1000;
        private readonly Queue<Tuple<int, long>> colaDesvios = new Queue<Tuple<int, long>>();

        private bool PuertoDisponible()
        {
            lock (candadoSerial) return puertoArduino != null && puertoArduino.IsOpen;
        }

        private async Task ConectarArduinoAsync()
        {
            if (conectandoArduino || cerrando || PuertoDisponible()) return;
            conectandoArduino = true;
            try
            {
                lblEstado.Text = "Buscando Arduino Mega por USB...";
                SerialPort encontrado = await Task.Run(() =>
                {
                    foreach (string nombre in SerialPort.GetPortNames())
                    {
                        SerialPort candidato = null;
                        try
                        {
                            candidato = new SerialPort(nombre, 9600)
                            {
                                NewLine = "\n",
                                ReadTimeout = 600,
                                WriteTimeout = 500,
                                DtrEnable = true
                            };
                            candidato.Open();
                            Thread.Sleep(1800); // el Mega puede reiniciarse al abrir COM
                            candidato.DiscardInBuffer();
                            candidato.Write("?");
                            string respuesta = candidato.ReadLine().Trim();
                            if (respuesta.StartsWith("STATUS:", StringComparison.Ordinal))
                                return candidato;
                        }
                        catch { /* intentar siguiente COM */ }
                        candidato?.Dispose();
                    }
                    return null;
                });
                if (cerrando) { encontrado?.Dispose(); return; }
                if (encontrado == null)
                {
                    estadoArduino = "SIN_CONEXION";
                    lblEstado.Text = "Arduino no encontrado (9600 baudios). Comprueba cable y puerto COM.";
                    return;
                }
                lock (candadoSerial) puertoArduino = encontrado;
                ultimaRespuestaArduino = (long)Environment.TickCount;
                encontrado.DataReceived += Arduino_DataReceived;
                EnviarArduino('?');
                lblEstado.Text = "Arduino detectado en " + encontrado.PortName + ". Pulsa Iniciar sistema.";
            }
            catch (Exception ex)
            {
                estadoArduino = "SIN_CONEXION";
                lblEstado.Text = "Arduino: " + ex.Message;
            }
            finally { conectandoArduino = false; }
        }

        private void EnviarArduino(char comando)
        {
            lock (candadoSerial)
            {
                try { if (puertoArduino != null && puertoArduino.IsOpen) puertoArduino.Write(comando.ToString()); }
                catch (Exception ex)
                {
                    estadoArduino = "LINK_LOST";
                    errorSalida = "USB Arduino: " + ex.Message;
                    contando = false;
                    Interlocked.Increment(ref revisionConteo);
                }
            }
        }

        private void Arduino_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var port = (SerialPort)sender;
                while (port.IsOpen && port.BytesToRead > 0)
                {
                    string linea = port.ReadLine().Trim();
                    ultimaRespuestaArduino = (long)Environment.TickCount;
                    if (IsDisposed || !IsHandleCreated || cerrando) return;
                    BeginInvoke(new Action(() => InterpretarArduino(linea)));
                }
            }
            catch (TimeoutException) { }
            catch (Exception) { estadoArduino = "LINK_LOST"; }
        }

        private void InterpretarArduino(string respuesta)
        {
            if (cerrando) return;
            if (respuesta == "EMERGENCY" || respuesta == "STATUS:EMERGENCY")
            {
                CambiarEstadoArduino("EMERGENCY");
            }
            else if (respuesta == "LINK_LOST") CambiarEstadoArduino("LINK_LOST");
            else if (respuesta == "RUNNING" || respuesta == "STATUS:RUNNING")
            {
                // No aceptar confirmaciones de marcha si se perdió la BD.
                if (!bdLista) { EnviarArduino('s'); return; }
                CambiarEstadoArduino("RUNNING");
            }
            else if (respuesta == "STOPPED" || respuesta == "STATUS:STOPPED" || respuesta == "READY")
                CambiarEstadoArduino("STOPPED");
            else if (respuesta == "STARTED_A") servoAOcupado = true;
            else if (respuesta == "STARTED_B") servoBOcupado = true;
            else if (respuesta == "DONE_A") servoAOcupado = false;
            else if (respuesta == "DONE_B") servoBOcupado = false;
            else if (respuesta == "SONG_START")
            {
                CambiarEstadoArduino("EMERGENCY");
                lblEstado.Text = "Función especial en Arduino; banda detenida.";
            }
            else if (respuesta == "SONG_END") lblEstado.Text = "Función especial finalizada; rearme requerido.";
            else if (respuesta == "REJECTED") lblEstado.Text = "Arduino: pieza no registrada; tercer contenedor.";
            else if (respuesta == "BUSY_A") servoAOcupado = true;
            else if (respuesta == "BUSY_B") servoBOcupado = true;
            else if (respuesta.StartsWith("ERROR:", StringComparison.Ordinal))
                errorSalida = "Arduino: " + respuesta;
            DespacharClasificaciones();
        }

        private void CambiarEstadoArduino(string nuevo)
        {
            if (estadoArduino == nuevo) return; // los mensajes STATUS no reinician seguimiento
            estadoArduino = nuevo;
            bool activo = nuevo == "RUNNING";
            contando = activo && bdLista;
            Interlocked.Increment(ref revisionConteo);
            if (!activo)
            {
                servoAOcupado = servoBOcupado = false;
                colaDesvios.Clear();
                cronometro.Stop();
            }
            else if (hayImagenUtil) cronometro.Start();
            btnIniciar.Text = activo ? "Detener banda" : "Iniciar sistema";
            btnIniciar.BackColor = activo ? Color.Khaki : Color.LightGreen;
            btnIniciar.Enabled = bdLista && PuertoDisponible();
            btnReiniciar.Enabled = bdLista && !activo;
            lblEstado.Text = nuevo == "EMERGENCY" ? "EMERGENCIA FÍSICA. Libera el pulsador y rearma con Iniciar." :
                nuevo == "LINK_LOST" ? "Sin comunicación. Revisa USB." :
                activo ? "Banda activa, clasificación y MySQL habilitados." : "Banda detenida; cámara activa.";
        }

        private void EncolarClasificacionArduino(int categoria)
        {
            if (estadoArduino != "RUNNING") return;
            if (categoria == 2) { EnviarArduino('c'); return; }
            colaDesvios.Enqueue(Tuple.Create(categoria, (long)Environment.TickCount));
            DespacharClasificaciones();
        }

        private void DespacharClasificaciones()
        {
            if (estadoArduino != "RUNNING" || !PuertoDisponible()) return;
            // Conserva el orden de detección; si el desviador está ocupado,
            // descarta una orden tardía EN LUGAR de actuar sobre otra pieza.
            while (colaDesvios.Count > 0)
            {
                var pieza = colaDesvios.Peek();
                if ((long)Environment.TickCount - pieza.Item2 > LimiteEsperaOrdenMs)
                {
                    colaDesvios.Dequeue();
                    errorSalida = "Orden de desvío descartada: servo ocupado; revisar separación entre piezas.";
                    continue;
                }
                bool esTuerca = pieza.Item1 == 1;
                if (esTuerca ? servoAOcupado : servoBOcupado) break;
                colaDesvios.Dequeue();
                if (esTuerca) servoAOcupado = true;
                else servoBOcupado = true;
                EnviarArduino(esTuerca ? 'a' : 'b');
            }
        }

        private void MantenerArduino()
        {
            if (!PuertoDisponible()) return;
            long ahora = (long)Environment.TickCount;
            if (ahora - ultimoPingArduino >= 500)
            {
                ultimoPingArduino = ahora;
                EnviarArduino('?');
            }
            if (ahora - ultimaRespuestaArduino > 3500 && estadoArduino == "RUNNING")
            {
                // Arduino también tiene watchdog independiente del PC.
                CambiarEstadoArduino("LINK_LOST");
            }
            DespacharClasificaciones();
        }

        private void DetenerArduinoYDesconectar()
        {
            EnviarArduino('s');
            lock (candadoSerial)
            {
                if (puertoArduino != null)
                {
                    try { puertoArduino.DataReceived -= Arduino_DataReceived; puertoArduino.Close(); }
                    catch { }
                    puertoArduino.Dispose();
                    puertoArduino = null;
                }
            }
        }
        #endregion

        // =========================================================================================
        #region MÓDULO DE ARRANQUE Y APAGADO AUTOMÁTICO (SEPARADO AL FINAL)
        // =========================================================================================

        // Ejecución interna: servicios externos y ciclo de vida de la aplicación.
        // La clasificación y el conteo se encuentran en la sección principal.
        private Task tareaServicios = Task.CompletedTask;
        private bool cerrando;
        private bool cierrePermitido;
        private bool apagadoSolicitado;
        private bool serviciosPreparados;
        private bool reiniciandoServicios;
        private string errorServicios;
        private Process procesoObs;
        private Process procesoScrcpy;

        // Captura interna: conserva un solo fotograma disponible para el análisis.
        // Si llega otro mientras se procesa, reemplaza al pendiente para evitar una cola.
        private sealed class FotogramaReciente : IDisposable
        {
            private readonly object candado = new object();
            private readonly AutoResetEvent disponible = new AutoResetEvent(false);
            private readonly CancellationTokenSource detener;
            private readonly Task lector;
            private Mat ultimo;
            private Exception error;
            private bool finalizado;
            private long instante;

            public FotogramaReciente(VideoCapture captura, CancellationToken token)
            {
                detener = CancellationTokenSource.CreateLinkedTokenSource(token);
                lector = Task.Run(() =>
                {
                    using (Mat imagen = new Mat())
                    {
                        try
                        {
                            int fallos = 0;
                            while (!detener.IsCancellationRequested)
                            {
                                if (!captura.Read(imagen) || imagen.Empty())
                                {
                                    if (++fallos >= 30)
                                        throw new InvalidOperationException("Se perdió la imagen de OBS.");
                                    if (detener.Token.WaitHandle.WaitOne(100)) break;
                                    continue;
                                }
                                fallos = 0;
                                long recibido = Stopwatch.GetTimestamp();
                                Mat copia = imagen.Clone();
                                lock (candado)
                                {
                                    ultimo?.Dispose();
                                    ultimo = copia;
                                    instante = recibido;
                                }
                                disponible.Set();
                            }
                        }
                        catch (Exception ex) { lock (candado) error = ex; }
                        finally
                        {
                            lock (candado) finalizado = true;
                            disponible.Set();
                        }
                    }
                });
            }

            public bool Tomar(Mat destino, out int esperaMs)
            {
                esperaMs = 0;
                while (!detener.IsCancellationRequested)
                {
                    lock (candado)
                    {
                        if (error != null) throw new InvalidOperationException(error.Message, error);
                        if (ultimo != null)
                        {
                            ultimo.CopyTo(destino);
                            ultimo.Dispose();
                            ultimo = null;
                            esperaMs = (int)((Stopwatch.GetTimestamp() - instante)
                                * 1000.0 / Stopwatch.Frequency);
                            return true;
                        }
                        if (finalizado) return false;
                    }
                    disponible.WaitOne(50);
                }
                return false;
            }

            public void Dispose()
            {
                detener.Cancel();
                lector.GetAwaiter().GetResult();
                lock (candado) { ultimo?.Dispose(); ultimo = null; }
                disponible.Dispose();
                detener.Dispose();
            }
        }

        private async void relojInterfaz_Tick(object sender, EventArgs e)
        {
            ActualizarEstadisticas();
            if (cerrando) return;
            MantenerArduino();
            _ = ActualizarPanelBDSiCorrespondeAsync();
            if (capturando) RecuperarVisionMinimizada();
            if (!serviciosPreparados || reiniciandoServicios) return;
            if (!ExisteProceso("scrcpy") || !ExisteProceso("obs64"))
            {
                reiniciandoServicios = true;
                try
                {
                    tareaServicios = IniciarServiciosAsync();
                    await tareaServicios;
                }
                finally { reiniciandoServicios = false; }
            }
        }

        private void Form1_Activated(object sender, EventArgs e)
        {
            if (capturando && !cerrando) EnviarVisionAlFondo();
        }

        // Ejecución de cámara: lectura continua y recuperación ante fallos.
        private void IniciarCamara()
        {
            if (capturando || cerrando) return;
            cancelacion = new CancellationTokenSource();
            capturando = true;
            cmbCamaras.Enabled = false;
            btnIniciar.Enabled = bdLista && PuertoDisponible();
            btnIniciar.Text = "Iniciar sistema";
            btnIniciar.BackColor = Color.LightGreen;
            btnReiniciar.Enabled = false;
            tareaVideo = EjecutarCapturaAsync(cancelacion.Token);
        }

        private async Task EjecutarCapturaAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        List<string> camaras = await Task.Run(() => EnumerarCamaras());
                        token.ThrowIfCancellationRequested();
                        cmbCamaras.Items.Clear();
                        foreach (string nombre in camaras) cmbCamaras.Items.Add(nombre);
                        int indice = camaras.FindIndex(nombre =>
                            nombre.IndexOf("OBS", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (indice < 0)
                            throw new InvalidOperationException(
                                "No aparece OBS Virtual Camera. Revisa su instalación y OBS.");
                        cmbCamaras.SelectedIndex = indice;
                        lblEstado.Text = "Conectando con " + camaras[indice] + "...";

                        await Task.Run(() =>
                        {
                            // La enumeración y la apertura usan el mismo sistema: DirectShow.
                            using (VideoCapture captura = new VideoCapture(
                                indice, VideoCaptureAPIs.DSHOW))
                            {
                                if (!captura.IsOpened())
                                    throw new InvalidOperationException(
                                        "No se pudo abrir OBS Virtual Camera.");
                                captura.Set(VideoCaptureProperties.FrameWidth, 1280);
                                captura.Set(VideoCaptureProperties.FrameHeight, 720);
                                ProcesarVideo(captura, token);
                            }
                        }, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        hayImagenUtil = false;
                        cronometro.Stop();
                        if (!cerrando)
                            lblEstado.Text = "Reconectando: " + ex.Message;
                    }
                    if (!token.IsCancellationRequested)
                        await Task.Delay(2000, token);
                }
            }
            catch (OperationCanceledException)
            {
                // La cancelación solo se solicita durante el apagado.
            }
            finally
            {
                capturando = false;
                hayImagenUtil = false;
                cronometro.Stop();
                cancelacion?.Dispose();
                cancelacion = null;
            }
        }

        private async Task DetenerCamaraAsync()
        {
            // Este método se utiliza únicamente al apagar el sistema.
            cancelacion?.Cancel();
            await tareaVideo;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (DesignMode) return;
            chkMostrarMascara_CheckedChanged(this, EventArgs.Empty);

            valorUmbral = trackUmbral.Value;
            ActualizarEstadisticas();
            relojInterfaz.Start();

            // Ajusta el tamaño inicial al área disponible.
            System.Drawing.Rectangle area =
                Screen.FromControl(this).WorkingArea;
            if (Width > area.Width || Height > area.Height)
            {
                Size = new System.Drawing.Size(
                    Math.Min(Width, area.Width - 20),
                    Math.Min(Height, area.Height - 20));
                Location = new System.Drawing.Point(
                    area.Left + (area.Width - Width) / 2,
                    area.Top + (area.Height - Height) / 2);
            }

            tareaServicios = IniciarServiciosAsync();
            await tareaServicios;
            if (!cerrando)
            {
                serviciosPreparados = true;
                IniciarCamara();
                // MySQL y Arduino se preparan en segundo plano DESPUÉS de iniciar la cámara.
                _ = InicializarIntegracionesAsync();
            }
        }


        private async Task InicializarIntegracionesAsync()
        {
            try
            {
                await InicializarBDAsync();
                if (cerrando) return;
                await ConectarArduinoAsync();
                if (!cerrando)
                {
                    btnIniciar.Enabled = bdLista && PuertoDisponible();
                    btnIniciar.Text = "Iniciar sistema";
                }
            }
            catch (Exception ex)
            {
                if (!cerrando) lblEstado.Text = "Integración: " + ex.Message;
            }
        }

        // Cámaras instaladas: nombres reales y orden de DirectShow.
        [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumeradorDispositivos
        {
            [PreserveSig]
            int CreateClassEnumerator(ref Guid categoria,
                out System.Runtime.InteropServices.ComTypes.IEnumMoniker enumerador,
                int opciones);
        }

        [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropiedadesDispositivo
        {
            [PreserveSig]
            int Read([MarshalAs(UnmanagedType.LPWStr)] string nombre,
                [MarshalAs(UnmanagedType.Struct)] out object valor, IntPtr registro);
            [PreserveSig]
            int Write([MarshalAs(UnmanagedType.LPWStr)] string nombre,
                [MarshalAs(UnmanagedType.Struct)] ref object valor);
        }

        private List<string> EnumerarCamaras()
        {
            List<string> nombresCamaras = new List<string>();
            object dispositivo = null;
            System.Runtime.InteropServices.ComTypes.IEnumMoniker enumerador = null;
            try
            {
                Type tipo = Type.GetTypeFromCLSID(
                    new Guid("62BE5D10-60EB-11D0-BD3B-00A0C911CE86"));
                dispositivo = Activator.CreateInstance(tipo);
                Guid categoria = new Guid("860BB310-5D01-11D0-BD3B-00A0C911CE86");
                int resultado = ((IEnumeradorDispositivos)dispositivo)
                    .CreateClassEnumerator(ref categoria, out enumerador, 0);
                if (resultado < 0) Marshal.ThrowExceptionForHR(resultado);
                if (enumerador == null) return nombresCamaras;
                var elementos = new System.Runtime.InteropServices.ComTypes.IMoniker[1];
                while (enumerador.Next(1, elementos, IntPtr.Zero) == 0)
                {
                    object propiedades = null;
                    string nombre = "Dispositivo " + nombresCamaras.Count;
                    try
                    {
                        Guid id = typeof(IPropiedadesDispositivo).GUID;
                        elementos[0].BindToStorage(null, null, ref id, out propiedades);
                        object valor;
                        if (((IPropiedadesDispositivo)propiedades)
                            .Read("FriendlyName", out valor, IntPtr.Zero) == 0)
                            nombre = Convert.ToString(valor);
                    }
                    catch (COMException)
                    {
                        // Conserva su posición aunque no se pueda leer el nombre.
                    }
                    finally
                    {
                        if (propiedades != null) Marshal.ReleaseComObject(propiedades);
                        Marshal.ReleaseComObject(elementos[0]);
                    }
                    nombresCamaras.Add(nombre);
                }
            }
            finally
            {
                if (enumerador != null) Marshal.ReleaseComObject(enumerador);
                if (dispositivo != null) Marshal.ReleaseComObject(dispositivo);
            }
            return nombresCamaras;
        }

        // PASO 1: mantener scrcpy restaurado en segundo plano.
        // Se evita minimizarlo para conservar la captura de ventana de OBS.

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string clase, string titulo);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr ventana);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr ventana, uint mensaje,
            IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr ventana, int comando);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr ventana, IntPtr posicion,
            int x, int y, int ancho, int alto, uint opciones);

        private void EnviarVisionAlFondo()
        {
            if (cerrando) return;
            IntPtr ventana = FindWindow(null, "Vision_Artificial");
            if (ventana == IntPtr.Zero) return;
            if (IsIconic(ventana)) ShowWindowAsync(ventana, 4);
            // Mantiene tamaño y posición; no activa la ventana.
            const uint opciones = 0x0001 | 0x0002 | 0x0010 | 0x0040 | 0x4000;
            SetWindowPos(ventana, new IntPtr(1), 0, 0, 0, 0, opciones);
        }

        private void RecuperarVisionMinimizada()
        {
            IntPtr ventana = FindWindow(null, "Vision_Artificial");
            if (ventana != IntPtr.Zero && IsIconic(ventana))
                EnviarVisionAlFondo();
        }

        // PASO 2: comprobar procesos y limpiar el aviso pendiente de OBS.
        private bool ExisteProceso(string nombre)
        {
            Process[] procesos = Process.GetProcessesByName(nombre);
            bool existe = procesos.Length > 0;
            foreach (Process proceso in procesos) proceso.Dispose();
            return existe;
        }

        private void LimpiarAvisoAnteriorObs()
        {
            if (ExisteProceso("obs64")) return;
            string carpeta = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "obs-studio", ".sentinel");
            if (!System.IO.Directory.Exists(carpeta)) return;
            foreach (string archivo in System.IO.Directory.GetFiles(carpeta))
                System.IO.File.Delete(archivo);
        }

        // PASO 3: iniciar OBS y scrcpy.
        private async Task IniciarServiciosAsync()
        {
            List<string> avisos = new List<string>();
            try
            {
                if (!ExisteProceso("obs64"))
                {
                    LimpiarAvisoAnteriorObs();
                    procesoObs?.Dispose();
                    procesoObs = Process.Start(new ProcessStartInfo
                    {
                        FileName = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                        WorkingDirectory = @"C:\Program Files\obs-studio\bin\64bit",
                        Arguments = "--startvirtualcam --minimize-to-tray",
                        UseShellExecute = true,
                        WindowStyle = ProcessWindowStyle.Minimized
                    });
                    await Task.Delay(2500);
                }
            }
            catch (Exception ex) { avisos.Add("OBS: " + ex.Message); }
            if (cerrando) return;

            try
            {
                if (!ExisteProceso("scrcpy"))
                {
                    procesoScrcpy?.Dispose();
                    // Ejecuta el comando de camara desde CMD, como proceso separado
                    // del formulario. No minimizar la ventana de scrcpy: OBS captura
                    // esa ventana y puede recibir negro si se minimiza.
                    procesoScrcpy = Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/D /C scrcpy.exe --render-driver=software " +
                            "--video-source=camera --camera-id=2 " +
                            "--camera-size=1280x720 --no-audio " +
                            "--window-title=Vision_Artificial",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }
                IntPtr ventana = IntPtr.Zero;
                // Esperar a que aparezca la ventana de scrcpy antes de moverla.
                // OBS necesita verla RESTAURADA (nunca minimizada).
                for (int intento = 0; intento < 60 && !cerrando; intento++)
                {
                    await Task.Delay(250);
                    ventana = FindWindow(null, "Vision_Artificial");
                    if (ventana != IntPtr.Zero) break;
                    if (procesoScrcpy != null && procesoScrcpy.HasExited &&
                        !ExisteProceso("scrcpy")) break;
                }
                if (ventana != IntPtr.Zero && !cerrando)
                {
                    // Espera de estabilizacion para que la imagen comience
                    // a llegar a OBS antes de enviarla al fondo.
                    await Task.Delay(2200);
                    EnviarVisionAlFondo();
                }
                else if (!cerrando)
                    avisos.Add("No apareció Vision_Artificial. Revisa CMD, USB, ADB y scrcpy.");
            }
            catch (Exception ex) { avisos.Add("scrcpy: " + ex.Message); }
            errorServicios = avisos.Count > 0 ? string.Join(" | ", avisos) : null;
            if (errorServicios != null && !cerrando)
                lblEstado.Text = errorServicios;
        }

        // PASO 4: cerrar únicamente los procesos iniciados por la aplicación.
        private async Task CerrarProcesoAsync(Process proceso,
            string nombre, bool permitirForzar)
        {
            if (proceso == null) return;
            try
            {
                if (proceso.HasExited) return;
                proceso.CloseMainWindow();
                bool termino = await Task.Run(() => proceso.WaitForExit(5000));
                if (!termino)
                {
                    if (permitirForzar) proceso.Kill();
                    else MessageBox.Show(nombre + " continúa abierto. " +
                        "Confirma su cierre o utiliza Archivo > Salir.",
                        "Cierre de servicios", MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo completar el cierre de " +
                    nombre + ": " + ex.Message);
            }
            finally { proceso.Dispose(); }
        }

        // PASO 5: detener la cámara y completar el cierre del formulario.
        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (cierrePermitido)
            {
                base.OnFormClosing(e);
                return;
            }
            e.Cancel = true;
            base.OnFormClosing(e);
            if (cerrando) return;
            if (!apagadoSolicitado && e.CloseReason == CloseReason.UserClosing)
            {
                // La X minimiza; el botón Apagar sistema finaliza la captura.
                WindowState = FormWindowState.Minimized;
                return;
            }
            cerrando = true;
            btnIniciar.Enabled = false;
            btnReiniciar.Enabled = false;
            btnApagar.Enabled = false;
            cmbCamaras.Enabled = false;
            lblEstado.Text = "Cerrando sistema...";
            DetenerArduinoYDesconectar();

            await tareaServicios;
            await DetenerCamaraAsync();
            if (bdLista) await EncolarBD(() => CerrarSesionBD());
            relojInterfaz.Stop();
            // CMD es el proceso padre; la ventana de scrcpy corresponde a
            // otro proceso. Solicitar cierre de esa ventana antes de cerrar CMD.
            IntPtr ventanaScrcpy = FindWindow(null, "Vision_Artificial");
            if (ventanaScrcpy != IntPtr.Zero) PostMessage(ventanaScrcpy, 0x0010, IntPtr.Zero, IntPtr.Zero);
            await CerrarProcesoAsync(procesoScrcpy, "CMD / scrcpy", true);
            await CerrarProcesoAsync(procesoObs, "OBS Studio", false);
            picOriginal.Image?.Dispose();
            pictMascara.Image?.Dispose();
            picOriginal.Image = null;
            pictMascara.Image = null;
            cierrePermitido = true;
            Close();
        }

        #endregion
        // =========================================================================================
    }
}