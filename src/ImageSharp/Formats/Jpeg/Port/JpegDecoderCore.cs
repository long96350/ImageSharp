﻿// <copyright file="JpegDecoderCore.cs" company="James Jackson-South">
// Copyright (c) James Jackson-South and contributors.
// Licensed under the Apache License, Version 2.0.
// </copyright>

namespace ImageSharp.Formats.Jpeg.Port
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Runtime.CompilerServices;

    using ImageSharp.Common.Extensions;
    using ImageSharp.Formats.Jpeg.Port.Components;
    using ImageSharp.Memory;
    using ImageSharp.PixelFormats;

    /// <summary>
    /// Performs the jpeg decoding operation.
    /// Ported from <see href="https://github.com/mozilla/pdf.js/blob/master/src/core/jpg.js"/>
    /// </summary>
    internal sealed class JpegDecoderCore : IDisposable
    {
        /// <summary>
        /// The decoder options.
        /// </summary>
        private readonly IDecoderOptions options;

        /// <summary>
        /// The global configuration
        /// </summary>
        private readonly Configuration configuration;

        /// <summary>
        /// Gets the temporary buffer used to store bytes read from the stream.
        /// </summary>
        private readonly byte[] temp = new byte[2 * 16 * 4];

        private readonly byte[] uint16Buffer = new byte[2];

        private QuantizationTables quantizationTables;

        private HuffmanTables dcHuffmanTables;

        private HuffmanTables acHuffmanTables;

        private Frame frame;

        private ComponentBlocks components;

        private JpegPixelArea pixelArea;

        private ushort resetInterval;

        private int imageWidth;

        private int imageHeight;

        private int numComponents;

        /// <summary>
        /// Contains information about the JFIF marker
        /// </summary>
        private JFif jFif;

        /// <summary>
        /// Contains information about the Adobe marker
        /// </summary>
        private Adobe adobe;

        /// <summary>
        /// Initializes static members of the <see cref="JpegDecoderCore"/> class.
        /// </summary>
        static JpegDecoderCore()
        {
            YCbCrToRgbTables.Create();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="JpegDecoderCore" /> class.
        /// </summary>
        /// <param name="options">The decoder options.</param>
        /// <param name="configuration">The configuration.</param>
        public JpegDecoderCore(IDecoderOptions options, Configuration configuration)
        {
            this.configuration = configuration ?? Configuration.Default;
            this.options = options ?? new DecoderOptions();
        }

        /// <summary>
        /// Gets the input stream.
        /// </summary>
        public Stream InputStream { get; private set; }

        /// <summary>
        /// Finds the next file marker within the byte stream. Not used but I'm keeping it for now for testing
        /// </summary>
        /// <param name="stream">The input stream</param>
        /// <returns>The <see cref="FileMarker"/></returns>
        public static FileMarker FindNextFileMarkerNew(Stream stream)
        {
            byte[] marker = new byte[2];

            int value = stream.Read(marker, 0, 2);

            if (value == 0)
            {
                return new FileMarker(JpegConstants.Markers.EOI, (int)stream.Length - 2);
            }

            if (marker[0] == JpegConstants.Markers.Prefix)
            {
                // According to Section B.1.1.2:
                // "Any marker may optionally be preceded by any number of fill bytes, which are bytes assigned code 0xFF."
                while (marker[1] == JpegConstants.Markers.Prefix)
                {
                    int suffix = stream.ReadByte();
                    if (suffix == -1)
                    {
                        return new FileMarker(JpegConstants.Markers.EOI, (int)stream.Length - 2);
                    }

                    marker[1] = (byte)value;
                }

                return new FileMarker((ushort)((marker[0] << 8) | marker[1]), (int)(stream.Position - 2));
            }

            return new FileMarker((ushort)((marker[0] << 8) | marker[1]), (int)(stream.Position - 2), true);
        }

        /// <summary>
        /// Finds the next file marker within the byte stream
        /// </summary>
        /// <param name="stream">The input stream</param>
        /// <returns>The <see cref="FileMarker"/></returns>
        public static FileMarker FindNextFileMarker(Stream stream)
        {
            byte[] buffer = new byte[2];
            long maxPos = stream.Length - 1;
            long currentPos = stream.Position;
            long newPos = currentPos;

            if (currentPos >= maxPos)
            {
                return new FileMarker(JpegConstants.Markers.EOI, (int)stream.Length, true);
            }

            int value = stream.Read(buffer, 0, 2);

            if (value == 0)
            {
                return new FileMarker(JpegConstants.Markers.EOI, (int)stream.Length, true);
            }

            ushort currentMarker = (ushort)((buffer[0] << 8) | buffer[1]);
            if (currentMarker >= JpegConstants.Markers.SOF0 && currentMarker <= JpegConstants.Markers.COM)
            {
                return new FileMarker(currentMarker, stream.Position - 2);
            }

            value = stream.Read(buffer, 0, 2);

            if (value == 0)
            {
                return new FileMarker(JpegConstants.Markers.EOI, (int)stream.Length, true);
            }

            ushort newMarker = (ushort)((buffer[0] << 8) | buffer[1]);
            while (!(newMarker >= JpegConstants.Markers.SOF0 && newMarker <= JpegConstants.Markers.COM))
            {
                if (++newPos >= maxPos)
                {
                    return new FileMarker(JpegConstants.Markers.EOI, (int)stream.Length, true);
                }

                stream.Read(buffer, 0, 2);
                newMarker = (ushort)((buffer[0] << 8) | buffer[1]);
            }

            return new FileMarker(newMarker, newPos, true);
        }

        /// <summary>
        /// Decodes the image from the specified <see cref="Stream"/>  and sets
        /// the data to image.
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="stream">The stream, where the image should be.</param>
        /// <returns>The decoded image.</returns>
        public Image<TPixel> Decode<TPixel>(Stream stream)
            where TPixel : struct, IPixel<TPixel>
        {
            this.InputStream = stream;
            this.ParseStream();

            var image = new Image<TPixel>(this.imageWidth, this.imageHeight);
            this.GetData(image);
            return image;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            this.frame?.Dispose();
            this.components?.Dispose();
            this.quantizationTables?.Dispose();
            this.pixelArea.Dispose();

            // Set large fields to null.
            this.frame = null;
            this.components = null;
            this.quantizationTables = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetBlockBufferOffset(ref Component component, int row, int col)
        {
            return 64 * (((component.BlocksPerLine + 1) * row) + col);
        }

        /// <summary>
        /// Parses the input stream for file markers
        /// </summary>
        private void ParseStream()
        {
            // Check for the Start Of Image marker.
            var fileMarker = new FileMarker(this.ReadUint16(), 0);
            if (fileMarker.Marker != JpegConstants.Markers.SOI)
            {
                throw new ImageFormatException("Missing SOI marker.");
            }

            ushort marker = this.ReadUint16();
            fileMarker = new FileMarker(marker, (int)this.InputStream.Position - 2);

            this.quantizationTables = new QuantizationTables();
            this.dcHuffmanTables = new HuffmanTables();
            this.acHuffmanTables = new HuffmanTables();

            while (fileMarker.Marker != JpegConstants.Markers.EOI)
            {
                // Get the marker length
                int remaining = this.ReadUint16() - 2;

                switch (fileMarker.Marker)
                {
                    case JpegConstants.Markers.APP0:
                        this.ProcessApplicationHeaderMarker(remaining);
                        break;

                    case JpegConstants.Markers.APP1:
                    case JpegConstants.Markers.APP2:
                    case JpegConstants.Markers.APP3:
                    case JpegConstants.Markers.APP4:
                    case JpegConstants.Markers.APP5:
                    case JpegConstants.Markers.APP6:
                    case JpegConstants.Markers.APP7:
                    case JpegConstants.Markers.APP8:
                    case JpegConstants.Markers.APP9:
                    case JpegConstants.Markers.APP10:
                    case JpegConstants.Markers.APP11:
                    case JpegConstants.Markers.APP12:
                    case JpegConstants.Markers.APP13:
                        this.InputStream.Skip(remaining);
                        break;

                    case JpegConstants.Markers.APP14:
                        this.ProcessApp14Marker(remaining);
                        break;

                    case JpegConstants.Markers.APP15:
                    case JpegConstants.Markers.COM:

                        // TODO: Read data block
                        this.InputStream.Skip(remaining);
                        break;

                    case JpegConstants.Markers.DQT:
                        this.ProcessDefineQuantizationTablesMarker(remaining);
                        break;

                    case JpegConstants.Markers.SOF0:
                    case JpegConstants.Markers.SOF1:
                    case JpegConstants.Markers.SOF2:
                        this.ProcessStartOfFrameMarker(remaining, fileMarker);
                        break;

                    case JpegConstants.Markers.DHT:
                        this.ProcessDefineHuffmanTablesMarker(remaining);
                        break;

                    case JpegConstants.Markers.DRI:
                        this.ProcessDefineRestartIntervalMarker(remaining);
                        break;

                    case JpegConstants.Markers.SOS:
                        this.ProcessStartOfScanMarker();
                        break;

                    case JpegConstants.Markers.XFF:
                        if ((byte)this.InputStream.ReadByte() != 0xFF)
                        {
                            // Avoid skipping a valid marker
                            this.InputStream.Position -= 1;
                        }

                        break;

                        //default:

                        //    // TODO: Not convinced this is required
                        //    // Skip back as it could be incorrect encoding -- last 0xFF byte of the previous
                        //    // block was eaten by the encoder
                        //    this.InputStream.Position -= 3;
                        //    this.InputStream.Read(this.temp, 0, 2);
                        //    if (this.temp[0] == 0xFF && this.temp[1] >= 0xC0 && this.temp[1] <= 0xFE)
                        //    {
                        //        // Rewind that last bytes we read
                        //        this.InputStream.Position -= 2;
                        //        break;
                        //    }

                        //    // throw new ImageFormatException($"Unknown Marker {fileMarker.Marker} at {fileMarker.Position}");
                        //    break;
                }

                // Read on. TODO: Test this on damaged images.
                fileMarker = FindNextFileMarkerNew(this.InputStream);
            }

            this.imageWidth = this.frame.SamplesPerLine;
            this.imageHeight = this.frame.Scanlines;
            this.components = new ComponentBlocks { Components = new Component[this.frame.ComponentCount] };

            for (int i = 0; i < this.components.Components.Length; i++)
            {
                ref var frameComponent = ref this.frame.Components[i];
                var component = new Component
                {
                    ScaleX = frameComponent.HorizontalFactor / (float)this.frame.MaxHorizontalFactor,
                    ScaleY = frameComponent.VerticalFactor / (float)this.frame.MaxVerticalFactor,
                    BlocksPerLine = frameComponent.BlocksPerLine,
                    BlocksPerColumn = frameComponent.BlocksPerColumn
                };

                this.BuildComponentData(ref component, ref frameComponent);
                this.components.Components[i] = component;
            }

            this.numComponents = this.components.Components.Length;
        }

        /// <summary>
        /// Fills the given image with the color data
        /// </summary>
        /// <typeparam name="TPixel">The pixel format.</typeparam>
        /// <param name="image">The image</param>
        private void GetData<TPixel>(Image<TPixel> image)
            where TPixel : struct, IPixel<TPixel>
        {
            if (this.numComponents > 4)
            {
                throw new ImageFormatException($"Unsupported color mode. Max components 4; found {this.numComponents}");
            }

            this.pixelArea = new JpegPixelArea(image.Width, image.Height, this.numComponents);
            this.pixelArea.LinearizeBlockData(this.components, image.Width, image.Height);

            if (this.numComponents == 1)
            {
                this.FillGrayScaleImage(image);
                return;
            }

            if (this.numComponents == 3)
            {
                if (this.adobe.Equals(default(Adobe)) || this.adobe.ColorTransform == JpegConstants.Markers.Adobe.ColorTransformYCbCr)
                {
                    this.FillYCbCrImage(image);
                }
                else if (this.adobe.ColorTransform == JpegConstants.Markers.Adobe.ColorTransformUnknown)
                {
                    this.FillRgbImage(image);
                }
            }

            if (this.numComponents == 4)
            {
                if (this.adobe.ColorTransform == JpegConstants.Markers.Adobe.ColorTransformYcck)
                {
                    this.FillYcckImage(image);
                }
                else
                {
                    this.FillCmykImage(image);
                }
            }
        }

        /// <summary>
        /// Processes the application header containing the JFIF identifier plus extra data.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessApplicationHeaderMarker(int remaining)
        {
            if (remaining < 5)
            {
                // Skip the application header length
                this.InputStream.Skip(remaining);
                return;
            }

            this.InputStream.Read(this.temp, 0, 13);
            remaining -= 13;

            bool isJfif = this.temp[0] == JpegConstants.Markers.JFif.J &&
                          this.temp[1] == JpegConstants.Markers.JFif.F &&
                          this.temp[2] == JpegConstants.Markers.JFif.I &&
                          this.temp[3] == JpegConstants.Markers.JFif.F &&
                          this.temp[4] == JpegConstants.Markers.JFif.Null;

            if (isJfif)
            {
                this.jFif = new JFif
                {
                    MajorVersion = this.temp[5],
                    MinorVersion = this.temp[6],
                    DensityUnits = this.temp[7],
                    XDensity = (short)((this.temp[8] << 8) | this.temp[9]),
                    YDensity = (short)((this.temp[10] << 8) | this.temp[11])
                };
            }

            // TODO: thumbnail
            if (remaining > 0)
            {
                this.InputStream.Skip(remaining);
            }
        }

        /// <summary>
        /// Processes the application header containing the Adobe identifier
        /// which stores image encoding information for DCT filters.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessApp14Marker(int remaining)
        {
            if (remaining < 12)
            {
                // Skip the application header length
                this.InputStream.Skip(remaining);
                return;
            }

            this.InputStream.Read(this.temp, 0, 12);
            remaining -= 12;

            bool isAdobe = this.temp[0] == JpegConstants.Markers.Adobe.A &&
                           this.temp[1] == JpegConstants.Markers.Adobe.D &&
                           this.temp[2] == JpegConstants.Markers.Adobe.O &&
                           this.temp[3] == JpegConstants.Markers.Adobe.B &&
                           this.temp[4] == JpegConstants.Markers.Adobe.E;

            if (isAdobe)
            {
                this.adobe = new Adobe
                {
                    DCTEncodeVersion = (short)((this.temp[5] << 8) | this.temp[6]),
                    APP14Flags0 = (short)((this.temp[7] << 8) | this.temp[8]),
                    APP14Flags1 = (short)((this.temp[9] << 8) | this.temp[10]),
                    ColorTransform = this.temp[11]
                };
            }

            if (remaining > 0)
            {
                this.InputStream.Skip(remaining);
            }
        }

        /// <summary>
        /// Processes the Define Quantization Marker and tables. Specified in section B.2.4.1.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        /// <exception cref="ImageFormatException">
        /// Thrown if the tables do not match the header
        /// </exception>
        private void ProcessDefineQuantizationTablesMarker(int remaining)
        {
            while (remaining > 0)
            {
                bool done = false;
                remaining--;
                int quantizationTableSpec = this.InputStream.ReadByte();

                if (quantizationTableSpec > 3)
                {
                    throw new ImageFormatException($"Bad Tq index value: {quantizationTableSpec}");
                }

                switch (quantizationTableSpec >> 4)
                {
                    case 0:
                        {
                            // 8 bit values
                            if (remaining < 64)
                            {
                                done = true;
                                break;
                            }

                            this.InputStream.Read(this.temp, 0, 64);
                            remaining -= 64;

                            Span<short> tableSpan = this.quantizationTables.Tables.GetRowSpan(quantizationTableSpec);
                            for (int j = 0; j < 64; j++)
                            {
                                tableSpan[QuantizationTables.DctZigZag[j]] = this.temp[j];
                            }
                        }

                        break;
                    case 1:
                        {
                            // 16 bit values
                            if (remaining < 128)
                            {
                                done = true;
                                break;
                            }

                            this.InputStream.Read(this.temp, 0, 128);
                            remaining -= 128;

                            Span<short> tableSpan = this.quantizationTables.Tables.GetRowSpan(quantizationTableSpec);
                            for (int j = 0; j < 64; j++)
                            {
                                tableSpan[QuantizationTables.DctZigZag[j]] = (short)((this.temp[2 * j] << 8) | this.temp[(2 * j) + 1]);
                            }
                        }

                        break;
                    default:
                        throw new ImageFormatException("Bad Tq index value");
                }

                if (done)
                {
                    break;
                }
            }

            if (remaining != 0)
            {
                throw new ImageFormatException("DQT has wrong length");
            }
        }

        /// <summary>
        /// Processes the Start of Frame marker.  Specified in section B.2.2.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        /// <param name="frameMarker">The current frame marker.</param>
        private void ProcessStartOfFrameMarker(int remaining, FileMarker frameMarker)
        {
            if (this.frame != null)
            {
                throw new ImageFormatException("Multiple SOF markers. Only single frame jpegs supported.");
            }

            this.InputStream.Read(this.temp, 0, remaining);

            this.frame = new Frame
            {
                Extended = frameMarker.Marker == JpegConstants.Markers.SOF1,
                Progressive = frameMarker.Marker == JpegConstants.Markers.SOF2,
                Precision = this.temp[0],
                Scanlines = (short)((this.temp[1] << 8) | this.temp[2]),
                SamplesPerLine = (short)((this.temp[3] << 8) | this.temp[4]),
                ComponentCount = this.temp[5]
            };

            int maxH = 0;
            int maxV = 0;
            int index = 6;

            // No need to pool this. They max out at 4
            this.frame.ComponentIds = new byte[this.frame.ComponentCount];
            this.frame.Components = new FrameComponent[this.frame.ComponentCount];

            for (int i = 0; i < this.frame.Components.Length; i++)
            {
                int h = this.temp[index + 1] >> 4;
                int v = this.temp[index + 1] & 15;

                if (maxH < h)
                {
                    maxH = h;
                }

                if (maxV < v)
                {
                    maxV = v;
                }

                ref var component = ref this.frame.Components[i];
                component.Id = this.temp[index];
                component.HorizontalFactor = h;
                component.VerticalFactor = v;
                component.QuantizationIdentifier = this.temp[index + 2];

                this.frame.ComponentIds[i] = component.Id;

                index += 3;
            }

            this.frame.MaxHorizontalFactor = maxH;
            this.frame.MaxVerticalFactor = maxV;
            this.PrepareComponents();
        }

        /// <summary>
        /// Processes a Define Huffman Table marker, and initializes a huffman
        /// struct from its contents. Specified in section B.2.4.2.
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessDefineHuffmanTablesMarker(int remaining)
        {
            if (remaining < 17)
            {
                throw new ImageFormatException($"DHT has wrong length: {remaining}");
            }

            using (var huffmanData = Buffer<byte>.CreateClean(16))
            {
                for (int i = 2; i < remaining;)
                {
                    byte huffmanTableSpec = (byte)this.InputStream.ReadByte();
                    this.InputStream.Read(huffmanData.Array, 0, 16);

                    using (var codeLengths = Buffer<byte>.CreateClean(16))
                    {
                        int codeLengthSum = 0;

                        for (int j = 0; j < 16; j++)
                        {
                            codeLengthSum += codeLengths[j] = huffmanData[j];
                        }

                        using (var huffmanValues = Buffer<byte>.CreateClean(codeLengthSum))
                        {
                            this.InputStream.Read(huffmanValues.Array, 0, codeLengthSum);

                            i += 17 + codeLengthSum;

                            this.BuildHuffmanTable(
                                huffmanTableSpec >> 4 == 0 ? this.dcHuffmanTables : this.acHuffmanTables,
                                huffmanTableSpec & 15,
                                codeLengths.Array,
                                huffmanValues.Array);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Processes the DRI (Define Restart Interval Marker) Which specifies the interval between RSTn markers, in
        /// macroblocks
        /// </summary>
        /// <param name="remaining">The remaining bytes in the segment block.</param>
        private void ProcessDefineRestartIntervalMarker(int remaining)
        {
            if (remaining != 2)
            {
                throw new ImageFormatException($"DRI has wrong length: {remaining}");
            }

            this.resetInterval = this.ReadUint16();
        }

        /// <summary>
        /// Processes the SOS (Start of scan marker).
        /// </summary>
        private void ProcessStartOfScanMarker()
        {
            int selectorsCount = this.InputStream.ReadByte();
            int componentIndex = -1;
            for (int i = 0; i < selectorsCount; i++)
            {
                componentIndex = -1;
                int selector = this.InputStream.ReadByte();

                for (int j = 0; j < this.frame.ComponentIds.Length; j++)
                {
                    byte id = this.frame.ComponentIds[j];
                    if (selector == id)
                    {
                        componentIndex = j;
                    }
                }

                if (componentIndex < 0)
                {
                    throw new ImageFormatException("Unknown component selector");
                }

                ref FrameComponent component = ref this.frame.Components[componentIndex];
                int tableSpec = this.InputStream.ReadByte();
                component.DCHuffmanTableId = tableSpec >> 4;
                component.ACHuffmanTableId = tableSpec & 15;
            }

            this.InputStream.Read(this.temp, 0, 3);

            int spectralStart = this.temp[0];
            int spectralEnd = this.temp[1];
            int successiveApproximation = this.temp[2];
            var scanDecoder = default(ScanDecoder);

            long position = scanDecoder.DecodeScan(
                 this.frame,
                 this.InputStream,
                 this.dcHuffmanTables,
                 this.acHuffmanTables,
                 this.frame.Components,
                 componentIndex,
                 selectorsCount,
                 this.resetInterval,
                 spectralStart,
                 spectralEnd,
                 successiveApproximation >> 4,
                 successiveApproximation & 15);

            this.InputStream.Position += position;

            Debug.WriteLine("spectralStart= " + spectralStart);
            Debug.WriteLine("spectralEnd= " + spectralEnd);
            Debug.WriteLine("successiveApproximation= " + successiveApproximation);
            Debug.WriteLine("Components after");
            //for (int i = 0; i < 3; i++)
            //{
            //    for (int j = 0; j < 10; j++)
            //    {
            //        Debug.WriteLine("component [" + i + "] : value [" + j + "] =" + this.frame.Components[i].BlockData[j] + "]");
            //    }
            //}
        }

        /// <summary>
        /// Build the data for the given component
        /// </summary>
        /// <param name="component">The component</param>
        /// <param name="frameComponent">The frame component</param>
        private void BuildComponentData(ref Component component, ref FrameComponent frameComponent)
        {
            int blocksPerLine = component.BlocksPerLine;
            int blocksPerColumn = component.BlocksPerColumn;
            using (var computationBuffer = Buffer<short>.CreateClean(64))
            {
                for (int blockRow = 0; blockRow < blocksPerColumn; blockRow++)
                {
                    for (int blockCol = 0; blockCol < blocksPerLine; blockCol++)
                    {
                        int offset = GetBlockBufferOffset(ref component, blockRow, blockCol);
                        IDCT.QuantizeAndInverse(this.quantizationTables, ref frameComponent, offset, computationBuffer);
                    }
                }
            }

            component.Output = frameComponent.BlockData;
        }

        /// <summary>
        /// Builds the huffman tables
        /// TODO: This is our bottleneck. We should use a faster algorithm with a LUT.
        /// </summary>
        /// <param name="tables">The tables</param>
        /// <param name="index">The table index</param>
        /// <param name="codeLengths">The codelengths</param>
        /// <param name="values">The values</param>
        private void BuildHuffmanTable(HuffmanTables tables, int index, byte[] codeLengths, byte[] values)
        {
            int length = 16;
            while (length > 0 && codeLengths[length - 1] == 0)
            {
                length--;
            }

            // TODO: Check the branch children capacity here. Seems to max at 2
            var code = new List<HuffmanBranch> { new HuffmanBranch(-1) };
            HuffmanBranch p = code[0];
            int k = 0;

            for (int i = 0; i < length; i++)
            {
                HuffmanBranch q;
                for (int j = 0; j < codeLengths[i]; j++)
                {
                    p = code.Pop();
                    p.Children[p.Index] = new HuffmanBranch(values[k]);
                    while (p.Index > 0)
                    {
                        p = code.Pop();
                    }

                    p.Index++;
                    code.Add(p);
                    while (code.Count <= i)
                    {
                        q = new HuffmanBranch(-1);
                        code.Add(q);
                        p.Children[p.Index] = new HuffmanBranch(q.Children);
                        p = q;
                    }

                    k++;
                }

                if (i + 1 < length)
                {
                    // p here points to last code
                    q = new HuffmanBranch(-1);
                    code.Add(q);
                    p.Children[p.Index] = new HuffmanBranch(q.Children);
                    p = q;
                }
            }

            tables[index] = code[0].Children;
        }

        /// <summary>
        /// Allocates the frame component blocks
        /// </summary>
        private void PrepareComponents()
        {
            int mcusPerLine = (int)MathF.Ceiling(this.frame.SamplesPerLine / 8F / this.frame.MaxHorizontalFactor);
            int mcusPerColumn = (int)MathF.Ceiling(this.frame.Scanlines / 8F / this.frame.MaxVerticalFactor);

            for (int i = 0; i < this.frame.ComponentCount; i++)
            {
                ref var component = ref this.frame.Components[i];
                int blocksPerLine = (int)MathF.Ceiling(MathF.Ceiling(this.frame.SamplesPerLine / 8F) * component.HorizontalFactor / this.frame.MaxHorizontalFactor);
                int blocksPerColumn = (int)MathF.Ceiling(MathF.Ceiling(this.frame.Scanlines / 8F) * component.VerticalFactor / this.frame.MaxVerticalFactor);
                int blocksPerLineForMcu = mcusPerLine * component.HorizontalFactor;
                int blocksPerColumnForMcu = mcusPerColumn * component.VerticalFactor;

                int blocksBufferSize = 64 * blocksPerColumnForMcu * (blocksPerLineForMcu + 1);

                // Pooled. Disposed via frame disposal
                component.BlockData = Buffer<short>.CreateClean(blocksBufferSize);
                component.BlocksPerLine = blocksPerLine;
                component.BlocksPerColumn = blocksPerColumn;
            }

            this.frame.McusPerLine = mcusPerLine;
            this.frame.McusPerColumn = mcusPerColumn;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FillGrayScaleImage<TPixel>(Image<TPixel> image)
            where TPixel : struct, IPixel<TPixel>
        {
            for (int y = 0; y < image.Height; y++)
            {
                Span<TPixel> imageRowSpan = image.GetRowSpan(y);
                Span<byte> areaRowSpan = this.pixelArea.GetRowSpan(y);

                for (int x = 0; x < image.Width; x++)
                {
                    ref byte luminance = ref areaRowSpan[x];
                    ref TPixel pixel = ref imageRowSpan[x];
                    var rgba = new Rgba32(luminance, luminance, luminance);
                    pixel.PackFromRgba32(rgba);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FillYCbCrImage<TPixel>(Image<TPixel> image)
            where TPixel : struct, IPixel<TPixel>
        {
            for (int y = 0; y < image.Height; y++)
            {
                Span<TPixel> imageRowSpan = image.GetRowSpan(y);
                Span<byte> areaRowSpan = this.pixelArea.GetRowSpan(y);
                for (int x = 0, o = 0; x < image.Width; x++, o += 3)
                {
                    ref byte yy = ref areaRowSpan[o];
                    ref byte cb = ref areaRowSpan[o + 1];
                    ref byte cr = ref areaRowSpan[o + 2];
                    ref TPixel pixel = ref imageRowSpan[x];
                    YCbCrToRgbTables.PackYCbCr(ref pixel, yy, cb, cr);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FillYcckImage<TPixel>(Image<TPixel> image)
            where TPixel : struct, IPixel<TPixel>
        {
            for (int y = 0; y < image.Height; y++)
            {
                Span<TPixel> imageRowSpan = image.GetRowSpan(y);
                Span<byte> areaRowSpan = this.pixelArea.GetRowSpan(y);
                for (int x = 0, o = 0; x < image.Width; x++, o += 4)
                {
                    ref byte yy = ref areaRowSpan[o];
                    ref byte cb = ref areaRowSpan[o + 1];
                    ref byte cr = ref areaRowSpan[o + 2];
                    ref byte k = ref areaRowSpan[o + 3];

                    ref TPixel pixel = ref imageRowSpan[x];
                    YCbCrToRgbTables.PackYccK(ref pixel, yy, cb, cr, k);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FillCmykImage<TPixel>(Image<TPixel> image)
            where TPixel : struct, IPixel<TPixel>
        {
            for (int y = 0; y < image.Height; y++)
            {
                Span<TPixel> imageRowSpan = image.GetRowSpan(y);
                Span<byte> areaRowSpan = this.pixelArea.GetRowSpan(y);
                for (int x = 0, o = 0; x < image.Width; x++, o += 4)
                {
                    ref byte c = ref areaRowSpan[o];
                    ref byte m = ref areaRowSpan[o + 1];
                    ref byte cy = ref areaRowSpan[o + 2];
                    ref byte k = ref areaRowSpan[o + 3];

                    byte r = (byte)((c * k) / 255);
                    byte g = (byte)((m * k) / 255);
                    byte b = (byte)((cy * k) / 255);

                    ref TPixel pixel = ref imageRowSpan[x];
                    var rgba = new Rgba32(r, g, b);
                    pixel.PackFromRgba32(rgba);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FillRgbImage<TPixel>(Image<TPixel> image)
            where TPixel : struct, IPixel<TPixel>
        {
            for (int y = 0; y < image.Height; y++)
            {
                Span<TPixel> imageRowSpan = image.GetRowSpan(y);
                Span<byte> areaRowSpan = this.pixelArea.GetRowSpan(y);

                PixelOperations<TPixel>.Instance.PackFromRgb24Bytes(areaRowSpan, imageRowSpan, image.Width);
            }
        }

        /// <summary>
        /// Reads a <see cref="ushort"/> from the stream advancing it by two bytes
        /// </summary>
        /// <returns>The <see cref="ushort"/></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort ReadUint16()
        {
            this.InputStream.Read(this.uint16Buffer, 0, 2);
            return (ushort)((this.uint16Buffer[0] << 8) | this.uint16Buffer[1]);
        }
    }
}