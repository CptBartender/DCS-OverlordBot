﻿using Ciribob.DCS.SimpleRadio.Standalone.Client.Network;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.Intents;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.LuisModels;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechOutput;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.Wave;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using FragLabs.Audio.Codecs;
using Ciribob.DCS.SimpleRadio.Standalone.Client.Audio.Managers;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.SpeechRecognition
{
    public class SpeechRecognitionListener
    {
        const string SUBSCRIPTION_KEY = "YourSubscriptionKey";
        const string REGION = "YourRegion";

        // Authorization token expires every 10 minutes. Renew it every 9 minutes.
        private static TimeSpan RefreshTokenDuration = TimeSpan.FromMinutes(9);


        // Creates an instance of a speech config with specified subscription key and service region.
        // Replace with your own subscription key and service region (e.g., "westus").
        private SpeechConfig _speechConfig;

        private readonly BufferedWaveProviderStreamReader _streamReader;
        private readonly SpeechRecognizer _recognizer;
        private readonly AudioConfig _audioConfig;
        private readonly OpusEncoder _encoder;

        public TCPVoiceHandler _voiceHandler;

        public int lastReceivedRadio = -1;

        public SpeechRecognitionListener(BufferedWaveProvider bufferedWaveProvider)
        {

            var authorizationToken = Task.Run(() => GetToken()).Result;

             _speechConfig = SpeechConfig.FromAuthorizationToken(authorizationToken, REGION);

            // If you are using Custom Speech endpoint
            _speechConfig.EndpointId = "YourEndpointId";

           _encoder = OpusEncoder.Create(AudioManager.INPUT_SAMPLE_RATE, 1, FragLabs.Audio.Codecs.Opus.Application.Voip);
           _encoder.ForwardErrorCorrection = false;
           _encoder.FrameByteCount(AudioManager.SEGMENT_FRAMES);

            _streamReader = new BufferedWaveProviderStreamReader(bufferedWaveProvider);
            _audioConfig = AudioConfig.FromStreamInput(_streamReader, AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1));

            _recognizer = new SpeechRecognizer(_speechConfig, _audioConfig);
        }

        // Gets an authorization token by sending a POST request to the token service.
        public static async Task<string> GetToken()
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", SUBSCRIPTION_KEY);
                UriBuilder uriBuilder = new UriBuilder("https://" + REGION + ".api.cognitive.microsoft.com/sts/v1.0/issueToken");

                using (var result = await client.PostAsync(uriBuilder.Uri.AbsoluteUri, null))
                {
                    Console.WriteLine("Token Uri: {0}", uriBuilder.Uri.AbsoluteUri);
                    if (result.IsSuccessStatusCode)
                    {
                        return await result.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        throw new HttpRequestException($"Cannot get token from {uriBuilder.ToString()}. Error: {result.StatusCode}");
                    }
                }
            }
        }

        // Renews authorization token periodically until cancellationToken is cancelled.
        public static Task StartTokenRenewTask(CancellationToken cancellationToken, SpeechRecognizer recognizer)
        {
            return Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(RefreshTokenDuration, cancellationToken);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        recognizer.AuthorizationToken = await GetToken();
                    }
                }
            });
        }

        public async Task StartListeningAsync()
        {
            var stopRecognition = new TaskCompletionSource<int>();
            CancellationTokenSource source = new CancellationTokenSource();

            var tokenRenewTask = StartTokenRenewTask(source.Token, _recognizer);

            _recognizer.Recognized += (s, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    Console.WriteLine($"RECOGNIZED: {e.Result.Text}");
                    string luisJson = Task.Run(() => LuisService.ParseIntent(e.Result.Text)).Result;

                    Console.WriteLine($"INTENT: {luisJson}");
                    LuisResponse luisResponse = JsonConvert.DeserializeObject<LuisResponse>(luisJson);
                    if(luisResponse.TopScoringIntent["intent"] == "RequestBogeyDope")
                    {
                        string response = Task.Run(() => RequestBogeyDope.Process(luisResponse)).Result;
                        Console.WriteLine($"RESPONSE: {response}");
                        byte[] audioResponse = Task.Run(() => Speaker.CreateResponse(response)).Result;
                        if (audioResponse != null)
                        {
                            SendResponse(audioResponse, audioResponse.Length);
                        }
                    }
                }
                else if (e.Result.Reason == ResultReason.NoMatch)
                {
                    Console.WriteLine($"NOMATCH: Speech could not be recognized.");
                }
            };

            _recognizer.Canceled += (s, e) =>
            {
                Console.WriteLine($"CANCELED: Reason={e.Reason}");

                if (e.Reason == CancellationReason.Error)
                {
                    Console.WriteLine($"CANCELED: ErrorCode={e.ErrorCode}");
                    Console.WriteLine($"CANCELED: ErrorDetails={e.ErrorDetails}");
                    Console.WriteLine($"CANCELED: Did you update the subscription info?");
                }

                stopRecognition.TrySetResult(0);
            };

            _recognizer.SessionStarted += (s, e) =>
            {
                Console.WriteLine("\nSession started event.");
            };

            _recognizer.SessionStopped += (s, e) =>
            {
                Console.WriteLine("\nSession stopped event.");
                Console.WriteLine("\nStop recognition.");
                stopRecognition.TrySetResult(0);
            };

            // Starts continuous recognition. Uses StopContinuousRecognitionAsync() to stop recognition.
            await _recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);

            // Waits for completion.
            // Use Task.WaitAny to keep the task rooted.
            Task.WaitAny(new[] { stopRecognition.Task });

            // Stops recognition.
            await _recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }

        // Expects a byte buffer containing 16 bit PCM WAV
        private void SendResponse(byte[] buffer, int length)
        {

            Queue<byte> audioQueue = new Queue<byte>(length);

            for (var i = 0; i < length; i++)
            {
                audioQueue.Enqueue(buffer[i]);
            }

            //read out the queue
            while (audioQueue.Count >= AudioManager.SEGMENT_FRAMES)
            {

                byte[] packetBuffer = new byte[AudioManager.SEGMENT_FRAMES];

                for (var i = 0; i < AudioManager.SEGMENT_FRAMES; i++)
                {
                    if (audioQueue.Count > 0)
                    {
                        packetBuffer[i] = audioQueue.Dequeue();
                    } else
                    {
                        packetBuffer[i] = 0;
                    }
                }

                //encode as opus bytes
                int len;
                var buff = _encoder.Encode(packetBuffer, AudioManager.SEGMENT_FRAMES, out len);

                if ((_voiceHandler != null) && (buff != null) && (len > 0))
                {
                    //create copy with small buffer
                    var encoded = new byte[len];

                    Buffer.BlockCopy(buff, 0, encoded, 0, len);

                    // Console.WriteLine("Sending: " + e.BytesRecorded);
                    _voiceHandler.Send(encoded, len, lastReceivedRadio);
                }
                else
                {
                    Console.WriteLine($"Invalid Bytes for Encoding - {length} should be {AudioManager.SEGMENT_FRAMES} ");
                }
            }
        }

    }
}