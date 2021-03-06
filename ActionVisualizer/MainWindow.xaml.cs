﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Exocortex.DSP;
using NAudio.Dsp;
using NAudio.Wave;
using VerySimpleKalman;
using System.Runtime.InteropServices;

// IF THESE ARE NOT FOUND, MAKE SURE TO ADD NUGET PACKAGE (WindowsInput) from online.
using WindowsInput;
using WindowsInput.Native;


namespace ActionVisualizer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        // Audio IO
        //private WasapiOut wOut;
        private WaveOut waveOut;
        private WaveIn waveIn;

        // Number of output channels
        public int waveOutChannels;

        // Empirically determined minimum frequency for two speaker configurations. 
        public int minFrequency = 18700;
        public int frequencyStep = 500;

        // Length of buffer gives ~24 hz updates.
        public int buffersize = 2048;
        
        // Variables for handling the input buffer.
        public int[] bin;
        public float[] sampledata;
        public float[] inbetween;
        bool init_inbetween = true;
        

        ComplexF[] indata;        
        double[] filteredindata;
        double[] priori;

        // Variables for storing data related to the specific channel information.
        int[] channelLabel;
        int[] velocity;
        int[] displacement;
        
        int[] prev_displacement;
        int[] instant_displacement;
        int[] towards_displacement;

        double ratio;
        VDKalman filter;

        // More helper variables for declaring 
        int selectedChannels = 1;
        List<int> frequencies;
        List<int> centerbins;

        // KF stores the individual channels, each of which extracts bandwidth shifts. (Essentially, each member is one Soundwave)
        List<KeyFrequency> KF;
        
        // Variables used for gesture recognition
        List<List<int>> history;
        List<List<int>> inverse_history;
        PointCollection pointHist;
        StylusPointCollection S;

        //Segmentation related variables.
        bool readyforgesture = false;
        bool gesture_started = false;
        int motion_free = 0;        
        int ignoreFrames = 0;

        // For keyboard command emulation.
        InputSimulator sim = new InputSimulator();

        public MainWindow()
        {
            InitializeComponent();
            this.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            this.KeyDown += new KeyEventHandler(MainWindow_KeyDown);

            // Print out all the possible input devices to console. Mostly for debugging.
            int waveInDevices = WaveIn.DeviceCount;
            for (int waveInDevice = 0; waveInDevice < waveInDevices; waveInDevice++)
            {
                WaveInCapabilities deviceInfo = WaveIn.GetCapabilities(waveInDevice);
                Console.WriteLine("Device {0}: {1}, {2} channels",
                    waveInDevice, deviceInfo.ProductName, deviceInfo.Channels);
            }

            // Instantiate a waveIn device and start recording.
            waveIn = new WaveIn();
            waveIn.BufferMilliseconds = 47 * buffersize / 2048;
            waveIn.DeviceNumber = 0;
            waveIn.WaveFormat = new WaveFormat(44100, 32, 1);
            waveIn.DataAvailable += waveIn_DataAvailable;

            try
            {
                waveIn.StartRecording();
            }
            catch (NAudio.MmException e)
            {
                Console.WriteLine(e.ToString() + "\nPlug in a microphone!");
            }

            history = new List<List<int>>();
            inverse_history = new List<List<int>>();
            pointHist = new PointCollection();

            bin = new int[buffersize * 2];
            sampledata = new float[buffersize * 2];
            priori = new double[buffersize * 2];

            
            //Initializing all the global variables to base values for 1 speaker configuration.
            channelLabel = new int[1];
            channelLabel[0] = 1;
            velocity = new int[1];
            velocity[0] = 0;

            prev_displacement = new int[1];
            prev_displacement[0] = 0;

            instant_displacement = new int[1];
            instant_displacement[0] = 0;

            towards_displacement = new int[1];
            towards_displacement[0] = 1;


            displacement = new int[1];
            displacement[0] = 0;

            for (int i = 0; i < buffersize * 2; i++)
            {
                bin[i] = i;
                sampledata[i] = 0;
                priori[i] = 0;

            }

            // Kalman filter related stuff.
            filter = new VDKalman(2);
            filter.initialize(1, .1, 1, 0);

            // To prevent problems with empty lists, we assume 1 channel to start.
            history.Add(new List<int> { 0 });
            inverse_history.Add(new List<int> { 0 });

            // Load up the classifier model file.
            WekaHelper.initialize();
        } 

        void waveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            //Console.WriteLine("WaveIn_DataAvailable");
            //Console.WriteLine(e.BytesRecorded); //8288 bytes -> 2072 floats (24 too many)
            
            // We have to concatenate consectutive buffers together to ensure that the time domain signal is continuous from the previous iteration.
            // Here, we grab the length of the inbetween buffer. 
            if (init_inbetween)
            {
                inbetween = new float[e.BytesRecorded / 4 - buffersize];
                for (int i = 0; i < e.BytesRecorded / 4 - buffersize; i++)
                    inbetween[i] = 0;
                init_inbetween = false;
            }

            // Here, we grab the byte array provided by the callback, and convert it to a float array.
            for (int index = 0; index < buffersize; index++)
            {
                int sample = (int)((e.Buffer[index * 4 + 3] << 24) |
                                        (e.Buffer[index * 4 + 2] << 16) |
                                        (e.Buffer[index * 4 + 1] << 8) |
                                        (e.Buffer[index * 4 + 0]));

                float sample32 = sample / 2147483648f;

                if (index >= (buffersize - inbetween.Length))
                    sampledata[index] = inbetween[index - buffersize + inbetween.Length];
                else
                    sampledata[index] = sampledata[index + buffersize + inbetween.Length];
                sampledata[index + buffersize] = sample32;

            }

            if (e.BytesRecorded / 4 - buffersize < 0)
                return;
            inbetween = new float[e.BytesRecorded / 4 - buffersize];

            // We then fill the inbetween buffer (extra data larger than the original buffer size) with the remainder.
            for (int i = buffersize; i < e.BytesRecorded / 4; i++)
            {
                int sample = (int)((e.Buffer[i * 4 + 3] << 24) |
                                        (e.Buffer[i * 4 + 2] << 16) |
                                        (e.Buffer[i * 4 + 1] << 8) |
                                        (e.Buffer[i * 4 + 0]));

                float sample32 = sample / 2147483648f;
                inbetween[i - buffersize] = sample32;
            }

            // bufferFFT grabs the sampledata buffer and calculates the Fourier transform, filters the data using a high-pass filter and stores it in filteredindata
            bufferFFT();

            // Updating the Kalman Filter
            filter.time_Update();

            // If the speakers are outputting the key tones.
            if ((waveOut != null))
            {

                KF = new List<KeyFrequency>();
                //gestureDetected.Text = "";
                
                for (int i = 0; i < frequencies.Count; i++)
                {
                    if (history[i].Count > 0)
                        KF.Add(new KeyFrequency(frequencies.ElementAt(i), i + 1, frequencyStep/30, filteredindata, centerbins.ElementAt(i), history[i].Last()));
                    else
                        KF.Add(new KeyFrequency(frequencies.ElementAt(i), i + 1, frequencyStep/30, filteredindata, centerbins.ElementAt(i), 0));

                    velocity[i] = KF.ElementAt(i).state;
                    filter.measurement_Update(velocity[i]);
                    displacement[i] += (int)filter.x_priori[0];

                    if ((displacement[i] - prev_displacement[i]) < 0 && velocity[i] > 0)
                    {

                        ratio = Math.Abs((double)((instant_displacement[i] - prev_displacement[i]) / (double)towards_displacement[i]));

                        prev_displacement[i] = displacement[i];
                    }
                    if ((displacement[i] - prev_displacement[i]) > 0 && velocity[i] < 0)
                    {
                        towards_displacement[i] = instant_displacement[i] - prev_displacement[i];
                        prev_displacement[i] = displacement[i];
                    }
                    instant_displacement[i] = displacement[i];

                    history[i].Add(velocity[i]);
                    inverse_history[i].Add(KF[i].inverse_state);

                }

                // Run through gesture detection.
                detectGestures();
            }
        }


        private void detectGestures()
        {
            ignoreFrames++;

            // Simple 1D gesture recognition heuristics.
            if (selectedChannels == 1)
            {
                foreach (List<int> subList in history)
                {
                    int signChanges = 0, bandwidth = 0, step = 0, lastSig = 0;
                    for (int i = 1; i < subList.Count; i++)
                    {
                        step++;
                        if (subList[i - 1] != 0)
                            lastSig = subList[i - 1];
                        if (subList[i] * lastSig < 0)
                        {
                            signChanges++;
                            bandwidth += step;
                            step = 0;
                        }
                    }
                    
                    if (KF[0].isBoth && KF[0].inverse_state > 5)
                        gestureDetected.Text = "Two Handed ";
                    else if (signChanges == 0 && (lastSig != 0))
                        gestureDetected.Text = "Scrolling ";
                    else if (signChanges == 2 || signChanges == 1)
                        gestureDetected.Text = "SingleTap ";
                    else if (signChanges >= 3)
                        gestureDetected.Text = "DoubleTap ";

                    // Naive segmentation, not a primary concern.
                    if (subList.Count > 25 && gestureDetected.Text != "")
                    {
                        gestureDetected.Text = "";
                        subList.Clear();
                    }
                }
            }
            // We generate the combined vector V from each channel and do various segmentation related things here.
            else if (selectedChannels == 2)
            {
                double tot_X = 0, tot_Y = 0;
                foreach (KeyFrequency now in KF)
                {
                    tot_X += now.x;
                    tot_Y += now.y;
                }

                pointHist.Add(new Point(tot_X, tot_Y));

                if (!gesture_started && tot_X == 0 && tot_Y == 0)
                {
                    pointHist.Clear();
                    foreach (List<int> sublist in history)
                        sublist.Clear();
                    foreach (List<int> sublist in inverse_history)
                        sublist.Clear();
                }
                if (gesture_started && tot_X == 0 && tot_Y == 0)
                    motion_free++;
                if (tot_X != 0 || tot_Y != 0)
                {
                    gesture_started = true;
                    motion_free = 0;                    
                }

                // create the stroke representation for recognition.
                generateStroke(pointHist);
            }           
            
            // Go check if a gesture was completed.
            gestureCompleted();
        }

        //creates stroke and draws it on the canvas.
        public void generateStroke(PointCollection pointHist)
        {
            S = new StylusPointCollection();
            S.Add(new StylusPoint(_ink.ActualWidth / 2, _ink.ActualHeight / 2));
            for (int i = 0; i < pointHist.Count; i++)
            {
                S.Add(new StylusPoint(S[i].X - pointHist[i].X, S[i].Y - pointHist[i].Y));
            }
            Stroke So = new Stroke(S);
            _ink.Strokes.Clear();
            _ink.Strokes.Add(So);

        }

        public void bufferFFT()
        {
            indata = new ComplexF[buffersize * 2];
            for (int i = 0; i < buffersize * 2; i++)
            {
                indata[i].Re = sampledata[i] * (float)FastFourierTransform.HammingWindow(i, buffersize * 2);
                indata[i].Im = 0;
            }
            Exocortex.DSP.Fourier.FFT(indata, buffersize * 2, Exocortex.DSP.FourierDirection.Forward);

            // The factor may need to be tuned (1.8 works on SP3).
            filteredindata = filterMean(indata, 1.8);
        }

        private void StartStopSineWave()
        {
            if (waveOut == null)
            {
                button1.Content = "Stop Sound";
                Console.WriteLine("User Selected Channels: " + selectedChannels);
                WaveOutCapabilities outdeviceInfo = WaveOut.GetCapabilities(0);
                waveOutChannels = outdeviceInfo.Channels;
                waveOut = new WaveOut();
                              
                int waveOutDevices = WaveOut.DeviceCount;
                for (int i = 0; i < waveOutDevices; i++)
                {
                    outdeviceInfo = WaveOut.GetCapabilities(i);
                    Console.WriteLine("Device {0}: {1}, {2} channels",
                            i, outdeviceInfo.ProductName, outdeviceInfo.Channels);                    
                }

                List<IWaveProvider> inputs = new List<IWaveProvider>();
                frequencies = new List<int>();
                centerbins = new List<int>();

                for (int c = 0; c < selectedChannels; c++)
                {
                        //Original Sine Wave generation
                        inputs.Add(new SineWaveProvider32(minFrequency + c * frequencyStep, 0.5f, 44100, 1));
                        frequencies.Add(minFrequency + c * frequencyStep);
                        centerbins.Add((int)Math.Round((minFrequency + c * frequencyStep) / 10.768));                    
                }

                var splitter = new MultiplexingWaveProvider(inputs, selectedChannels);
                try
                {
                    waveOut.Init(splitter);
                    waveOut.Play();
                }
                catch (System.ArgumentException)
                {
                    Console.WriteLine("Invalid audio channel count. Please select a lower number of audio channels");
                }

                //Console.WriteLine("Number of Channels: " + wOut.NumberOfBuffers);               
                Console.WriteLine("Number of Channels: " + waveOut.OutputWaveFormat.Channels);
            }
            else
            {
                waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
                button1.Content = "Start Sound";

                frequencies.Clear();
                centerbins.Clear();
            }
        }

        public float mag2db(ComplexF y)
        {
            return 20.0f * (float)Math.Log10(Math.Sqrt(y.Re * y.Re + y.Im * y.Im) / .02);
        }

        // Relative high pass filter. 
        public double[] filterMean(ComplexF[] data, double factor)
        {
            double[] outdata = new double[data.Length];
      
            double min = Double.PositiveInfinity;
            double mean = 0;
            for (int i = 0; i < data.Length; i++)
            {
                outdata[i] = mag2db(data[i]);
                min = Math.Min(outdata[i], min);
            }

            for (int i = 0; i < data.Length; i++)
            {
                outdata[i] -= min;
                mean += (outdata[i]);
            }
            mean /= data.Length;
            for (int i = 0; i < data.Length; i++)
                if (outdata[i] < (mean * factor))
                    outdata[i] = 0;

            for (int i = 0; i < data.Length; i++)
            {
                if ((i > 0) && (i < (data.Length - 1)))
                {
                    if ((outdata[i] > 0) && (priori[i] == 0) && (outdata[i - 1] == 0) && (outdata[i + 1] == 0))
                    {
                        outdata[i] = 0;
                    }
                }
                priori[i] = outdata[i];
            }            
            return outdata;
        }

        // House keeping for changing number of speakers. Fairly self explanatory.
        private void channelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (waveOut != null)
                StartStopSineWave();

            selectedChannels = (sender as ComboBox).SelectedIndex + 1;

            if (selectedChannels == 2)
            {
                _ink.Visibility = Visibility.Visible;
            }

            history.Clear();
            inverse_history.Clear();

            channelLabel = new int[selectedChannels];
            velocity = new int[selectedChannels];
            displacement = new int[selectedChannels];

            instant_displacement = new int[selectedChannels];
            prev_displacement = new int[selectedChannels];
            towards_displacement = new int[selectedChannels];

            for (int i = 0; i < selectedChannels; i++)
            {
                history.Add(new List<int> { 0 });
                inverse_history.Add(new List<int> { 0 });
                channelLabel[i] = i + 1;
                velocity[i] = 0;

                instant_displacement[i] = 0;
                prev_displacement[i] = 0;
                towards_displacement[i] = 1;

                displacement[i] = 0;
            }

        }

        private void button1_Click(object sender, RoutedEventArgs e)
        {
            StartStopSineWave();
        }
        
        bool chrome;

        public void gestureCompleted()
        {
            // Minimum number of frames of no motion to be segmented as a gesture
            int motion_threshold = 3; //originally 5         

            // Minimum length of time after a gesture is completed before another gesture can be started.
            int ignore_threshold = 10;

            // If users are still reseting their hands, ignore all the movements and clear all buffers.
            if (ignoreFrames <= ignore_threshold)
            {
                motion_free = 0;
                readyforgesture = false;
                colorBox.Background = new SolidColorBrush(Colors.Red);
                gesture_started = false;
                //Clear the buffers
                foreach (List<int> sublist in history)
                    sublist.Clear();
                foreach (List<int> sublist in inverse_history)
                    sublist.Clear();
                pointHist.Clear();
            }

            if (gesture_started && ignoreFrames > ignore_threshold && motion_free > motion_threshold && selectedChannels >= 2)
            {
                // Use LINQ to remove all the frames at the end that correspond to the motion free periods.
                pointHist = new PointCollection(pointHist.Reverse().Skip(motion_threshold).Reverse());
                S = new StylusPointCollection(S.Reverse().Skip(motion_threshold).Reverse());
                for (int i = 0; i < history.Count; i++)
                {
                    history[i] = new List<int>(history[i].Reverse<int>().Skip(motion_threshold).Reverse<int>());
                    inverse_history[i] = new List<int>(inverse_history[i].Reverse<int>().Skip(motion_threshold).Reverse<int>());
                }

                //If we are in detect mode, pass it to WEKA for classification.
                if (detectMode.IsChecked.Value && pointHist.Count > 9)
                {
                    //Call function to find features and test with weka machine
                    if (selectedChannels == 2)
                    {
                        float[] speakers = { (float)KF[0].speakerTheta, (float)KF[1].speakerTheta };
                        //temp stores the string identifier of the gesture
                        string temp = WekaHelper.Classify(false, pointHist.Count() * waveIn.BufferMilliseconds,
                            true, new List<float>(speakers), pointHist, S, history, inverse_history);

                        //switch statement to rename up/down gestures to forward/back when displaying in the application
                        switch (temp)
                        {
                            case "swipe_up":
                                temp = "swipe_forward";
                                break;
                            case "swipe_down":
                                temp = "swipe_back";
                                break;
                            case "tap_up":
                                temp = "tap_forward";
                                break;
                            case "tap_down":
                                temp = "tap_back";
                                break;
                            
                        }
                        gestureDetected.Text = temp;

                        //TODO Put interaction with other applications in this switch statement 
                        // Allows for changing between workspaces in windows 10.
                        if (shellIntegration.IsChecked.Value)
                        {
                            switch (temp)
                            {
                                case "swipe_forward":
                                    sim.Keyboard.KeyDown(VirtualKeyCode.LWIN);
                                    sim.Keyboard.KeyPress(VirtualKeyCode.TAB);
                                    sim.Keyboard.KeyUp(VirtualKeyCode.LWIN);
                                    break;
                                case "swipe_back":
                                    break;
                                case "swipe_left":
                                    if (!chrome)
                                    {
                                        sim.Keyboard.KeyDown(VirtualKeyCode.LWIN);
                                        sim.Keyboard.KeyDown(VirtualKeyCode.LCONTROL);
                                        sim.Keyboard.KeyPress(VirtualKeyCode.LEFT);
                                        sim.Keyboard.KeyUp(VirtualKeyCode.LWIN);
                                        sim.Keyboard.KeyUp(VirtualKeyCode.LCONTROL);
                                    }
                                    else
                                    {
                                        sim.Keyboard.KeyDown(VirtualKeyCode.LCONTROL);
                                        sim.Keyboard.KeyDown(VirtualKeyCode.LSHIFT);
                                        sim.Keyboard.KeyPress(VirtualKeyCode.TAB);
                                        sim.Keyboard.KeyUp(VirtualKeyCode.LSHIFT);
                                        sim.Keyboard.KeyUp(VirtualKeyCode.LCONTROL);
                                    }
                                    break;
                                case "swipe_right":
                                    if (!chrome)
                                    {
                                        sim.Keyboard.KeyDown(VirtualKeyCode.LWIN);
                                        sim.Keyboard.KeyDown(VirtualKeyCode.LCONTROL);
                                        sim.Keyboard.KeyPress(VirtualKeyCode.RIGHT);
                                        sim.Keyboard.KeyUp(VirtualKeyCode.LWIN);
                                        sim.Keyboard.KeyUp(VirtualKeyCode.LCONTROL);
                                    }
                                    else
                                    {
                                        sim.Keyboard.KeyDown(VirtualKeyCode.LCONTROL);
                                        sim.Keyboard.KeyPress(VirtualKeyCode.TAB);
                                        sim.Keyboard.KeyUp(VirtualKeyCode.LCONTROL);
                                    }
                                    break;
                                case "tap_forward":
                                    chrome = true;
                                    break;
                                case "tap_back":
                                    chrome = false;
                                    break;
                                case "tap_left":
                                    break;
                                case "tap_right":
                                    break;
                            }
                        }
                    }


                    ignoreFrames = 0;
                }
                // Clear the buffers
                foreach (List<int> sublist in history)
                    sublist.Clear();
                foreach (List<int> sublist in inverse_history)
                    sublist.Clear();
                pointHist.Clear();

                // Prepare for next gesture (might need a button press)
                readyforgesture = false;
                colorBox.Background = new SolidColorBrush(Colors.Red);
                gesture_started = false;
                motion_free = 0;
            }
        }    
        
        void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A)
            {
                readyforgesture = true;
                colorBox.Background = new SolidColorBrush(Colors.Green);
            }
            if (e.Key == Key.C)
            {
                _ink.Strokes.Clear();
            }

        }
          
        
    }
}
