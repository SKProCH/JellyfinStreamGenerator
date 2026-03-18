var showStreamGeneratorPopup = function (itemId, serverId) {
    const apiClient = window.ApiClient;

    const getFilteredCodecs = function (sourceCodecs, supportedTranscodingCodecs, baseCodecs, fallbackCodec) {
        const sourceCodecsLower = sourceCodecs.map(c => c.toLowerCase());
        const supportedLower = supportedTranscodingCodecs.map(c => c.toLowerCase());

        const filtered = baseCodecs.filter(c =>
            sourceCodecsLower.includes(c) || supportedLower.includes(c)
        );

        sourceCodecsLower.forEach(c => {
            if (!filtered.includes(c)) {
                filtered.push(c);
            }
        });

        if (fallbackCodec && !filtered.includes(fallbackCodec) && supportedLower.includes(fallbackCodec)) {
            filtered.push(fallbackCodec);
        }

        filtered.sort((a, b) => {
            if (a === fallbackCodec) return -1;
            if (b === fallbackCodec) return 1;
            return a.localeCompare(b);
        });

        return filtered;
    };

    const generateCodecCheckboxesHtml = function (codecs, inputName, checkboxLabelStyle) {
        let html = '';
        codecs.forEach(codec => {
            const label = codec.toUpperCase();
            html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="' + inputName + '" value="' + codec + '" checked style="margin-right: 5px;">' + label + '</label>';
        });
        return html;
    };

    /* Fetch media item and encoding options */
    Promise.all([
        apiClient.getItem(apiClient.getCurrentUserId(), itemId),
        apiClient.getJSON(apiClient.getUrl('Encoding/PublicOptions')).catch(() => ({
            TranscodingVideoCodecs: ['h264']
        }))
    ]).then(([item, encodingOptions]) => {
        if (!item || !item.MediaSources || item.MediaSources.length === 0) {
            alert("Cannot get media sources for this item.");
            return;
        }

        const mediaSource = item.MediaSources[0]; /* simplify: assume first media source */

        const checkboxLabelStyle = 'display: flex; align-items: center; cursor: pointer;';

        /* Filter Video Codecs */
        const sourceVideoCodecs = (mediaSource.MediaStreams || []).filter(s => s.Type === 'Video').map(s => s.Codec).filter(Boolean);
        const videoCodecs = getFilteredCodecs(sourceVideoCodecs, encodingOptions.TranscodingVideoCodecs || [], ['h264', 'hevc', 'av1', 'vp9'], 'h264');
        const videoCodecsHtml = generateCodecCheckboxesHtml(videoCodecs, 'videoCodec', checkboxLabelStyle);

        let maxBitrate = 140000000; // Default large number (140 Mbps)
        if (mediaSource.Bitrate) {
            maxBitrate = mediaSource.Bitrate;
        }

        /* Prepare Audio and Subtitle Options */
        let audioOptions = '<option value="">Default</option>';
        let subtitleOptions = '<option value="">None / Default</option>';

        if (mediaSource.MediaStreams) {
            mediaSource.MediaStreams.forEach(stream => {
                let name = stream.DisplayTitle || stream.Title || '';

                // If the name doesn't contain the language, prepend it
                if (stream.Language && stream.Language !== 'und') {
                    if (!name.toLowerCase().includes(stream.Language.toLowerCase())) {
                        name = stream.Language.toUpperCase() + (name ? ' - ' + name : '');
                    }
                }

                if (!name) {
                    name = 'Stream ' + stream.Index;
                }

                // Append codec only if not already in the name
                let codecStr = stream.Codec && !name.toLowerCase().includes(stream.Codec.toLowerCase()) ? (' [' + stream.Codec + ']') : '';

                if (stream.Type === 'Audio') {
                    audioOptions += '<option value="' + stream.Index + '">' + name + codecStr + '</option>';
                } else if (stream.Type === 'Subtitle') {
                    let typeStr = stream.IsExternal ? " (Ext)" : "";
                    let forcedStr = stream.IsForced && !name.toLowerCase().includes('forced') ? " (Forced)" : "";
                    let defaultStr = stream.IsDefault && !name.toLowerCase().includes('default') ? " (Default)" : "";

                    subtitleOptions += '<option value="' + stream.Index + '">' + name + codecStr + typeStr + forcedStr + defaultStr + '</option>';
                }
            });
        }

        /* Create Overlay */
        const overlay = document.createElement('div');
        overlay.style.position = 'fixed';
        overlay.style.top = '0';
        overlay.style.left = '0';
        overlay.style.width = '100%';
        overlay.style.height = '100%';
        overlay.style.backgroundColor = 'rgba(0, 0, 0, 0.7)';
        overlay.style.zIndex = '99999';
        overlay.style.display = 'flex';
        overlay.style.alignItems = 'center';
        overlay.style.justifyContent = 'center';
        overlay.style.fontFamily = 'sans-serif';

        /* Create Modal */
        const modal = document.createElement('div');
        modal.style.backgroundColor = '#1e1e1e';
        modal.style.color = '#fff';
        modal.style.padding = '20px';
        modal.style.borderRadius = '8px';
        modal.style.width = '450px';
        modal.style.maxWidth = '90%';
        modal.style.maxHeight = '90vh';
        modal.style.overflowY = 'auto';
        modal.style.boxShadow = '0 4px 20px rgba(0,0,0,0.5)';

        let html = '';
        html += '<h2 style="margin-top:0; margin-bottom: 20px; font-weight: normal;">Generate Stream URL</h2>';

        html += '<form id="streamGeneratorForm">';

        const selectStyle = 'width: 100%; padding: 8px; margin-top: 5px; margin-bottom: 15px; background: #333; color: #fff; border: 1px solid #444; border-radius: 4px; box-sizing: border-box;';

        const checkboxContainerStyle = 'display: flex; flex-wrap: wrap; gap: 10px; margin-top: 5px; margin-bottom: 15px; padding: 8px; background: #333; border: 1px solid #444; border-radius: 4px;';

        html += '<label>Video Codecs</label>';
        html += '<div style="' + checkboxContainerStyle + '">';
        html += videoCodecsHtml;
        html += '</div>';

        html += '<label>Audio Codecs</label>';
        html += '<div style="' + checkboxContainerStyle + '">';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="audioCodec" value="aac" checked style="margin-right: 5px;">AAC</label>';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="audioCodec" value="opus" checked style="margin-right: 5px;">OPUS</label>';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="audioCodec" value="flac" checked style="margin-right: 5px;">FLAC</label>';
        html += '</div>';

        const maxBitrateMbps = Math.max(1, Math.ceil(maxBitrate / 1000000));
        const sliderMax = Math.max(140, maxBitrateMbps); // Ensure slider can reach the item's bitrate if it's > 140

        html += '<label>Audio Stream<br>';
        html += '<select id="audioStreamIndex" style="' + selectStyle + '">';
        html += audioOptions;
        html += '</select></label>';

        html += '<label>Subtitle Stream<br>';
        html += '<select id="subtitleStreamIndex" style="' + selectStyle + '">';
        html += subtitleOptions;
        html += '</select></label>';

        html += '<label>Subtitle Method<br>';
        html += '<select id="subtitleMethod" style="' + selectStyle + '">';
        html += '<option value="Hls" selected>HLS</option>';
        html += '<option value="Encode">Burn In (Encode)</option>';
        html += '<option value="Embed">Embed</option>';
        html += '<option value="Drop">Drop</option>';
        html += '</select></label>';

        html += '<details style="margin-bottom: 15px; background: #333; padding: 10px; border-radius: 4px; border: 1px solid #444;">';
        html += '<summary style="cursor: pointer; font-weight: bold; margin-bottom: 5px;">Advanced</summary>';

        html += '<label style="display: block; margin-top: 10px;">Max Video Bitrate: <span id="bitrateDisplay">' + sliderMax + '</span> Mbps<br>';
        html += '<input type="range" id="maxVideoBitrate" style="' + selectStyle + ' cursor: pointer; margin-bottom: 0;" min="1" max="' + sliderMax + '" value="' + sliderMax + '"></label>';

        html += '<label style="display: flex; align-items: center; margin-top: 15px; cursor: pointer;">';
        html += '<input type="checkbox" id="copyTimestamps" style="margin-right: 8px;" checked />';
        html += '<span>Copy Timestamps</span>';
        html += '</label>';
        html += '</details>';

        html += '<label>Generated URL<br>';
        html += '<textarea id="txtOutputUrl" rows="4" style="width: 100%; padding: 8px; margin-top: 5px; background: #222; color: #aaa; border: 1px solid #444; border-radius: 4px; font-family: monospace; resize: none; box-sizing: border-box;" readonly></textarea></label>';

        const btnStyle = 'padding: 8px 16px; border: none; border-radius: 4px; cursor: pointer; font-weight: bold; margin-left: 10px;';

        html += '<div style="display: flex; justify-content: flex-end; margin-top: 20px;">';
        html += '<button type="button" id="btnCancel" style="' + btnStyle + ' background: #444; color: #fff;">Close</button>';
        html += '<button type="button" id="btnCopyUrl" style="' + btnStyle + ' background: #0078d7; color: #fff;">Copy URL</button>';
        html += '<button type="submit" style="' + btnStyle + ' background: #52b54b; color: #fff;">Generate</button>';
        html += '</div>';

        html += '</form>';

        modal.innerHTML = html;
        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        /* Handle close */
        const closePopup = function () {
            if (document.body.contains(overlay)) {
                document.body.removeChild(overlay);
            }
        };

        modal.querySelector('#btnCancel').addEventListener('click', closePopup);

        /* Handle Bitrate Slider Update */
        modal.querySelector('#maxVideoBitrate').addEventListener('input', function (e) {
            modal.querySelector('#bitrateDisplay').textContent = e.target.value;
        });

        /* Close on overlay click */
        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) closePopup();
        });

        /* Generate URL Logic */
        modal.querySelector('#streamGeneratorForm').addEventListener('submit', function (e) {
            e.preventDefault();

            const allVideoCheckboxes = Array.from(modal.querySelectorAll('input[name="videoCodec"]'));
            const allAudioCheckboxes = Array.from(modal.querySelectorAll('input[name="audioCodec"]'));

            const videoCodecCheckboxes = allVideoCheckboxes.filter(cb => cb.checked).map(cb => cb.value);
            const audioCodecCheckboxes = allAudioCheckboxes.filter(cb => cb.checked).map(cb => cb.value);

            const videoCodecsStr = videoCodecCheckboxes.join(',');
            const audioCodecsStr = audioCodecCheckboxes.join(',');

            const audioStreamIndex = modal.querySelector('#audioStreamIndex').value;
            const subtitleStreamIndex = modal.querySelector('#subtitleStreamIndex').value;
            const subtitleMethod = modal.querySelector('#subtitleMethod').value;
            const copyTimestamps = modal.querySelector('#copyTimestamps').checked;
            const maxVideoBitrate = parseInt(modal.querySelector('#maxVideoBitrate').value, 10) * 1000000;

            const serverUrl = apiClient.serverAddress();
            const deviceId = apiClient.deviceId();
            const playSessionId = 'stream_generator_' + Date.now();

            const queryParams = new URLSearchParams({
                deviceId: deviceId,
                playSessionId: playSessionId,
                api_key: apiClient.accessToken(),
                mediaSourceId: mediaSource.Id,
                static: false,
                enableAutoStreamCopy: true,
                allowVideoStreamCopy: true,
                allowAudioStreamCopy: true,
                copyTimestamps: copyTimestamps,
                videoBitrate: maxVideoBitrate
            });

            if (videoCodecsStr) queryParams.append('videoCodec', videoCodecsStr);
            if (audioCodecsStr) queryParams.append('audioCodec', audioCodecsStr);
            if (audioStreamIndex !== '') queryParams.append('audioStreamIndex', audioStreamIndex);
            if (subtitleStreamIndex !== '') {
                queryParams.append('subtitleStreamIndex', subtitleStreamIndex);
                queryParams.append('subtitleMethod', subtitleMethod);
            }

            const finalUrl = serverUrl + '/Videos/' + itemId + '/master.m3u8?' + decodeURIComponent(queryParams.toString());
            modal.querySelector('#txtOutputUrl').value = finalUrl;
        });

        /* Copy logic */
        modal.querySelector('#btnCopyUrl').addEventListener('click', function () {
            const outputStr = modal.querySelector('#txtOutputUrl').value;
            if (outputStr) {
                if (navigator.clipboard && window.isSecureContext) {
                    navigator.clipboard.writeText(outputStr).then(() => {
                        alert("URL copied to clipboard");
                    }).catch(err => {
                        alert("Failed to copy: " + err);
                    });
                } else {
                    const textArea = document.createElement("textarea");
                    textArea.value = outputStr;
                    textArea.style.position = "fixed";
                    textArea.style.left = "-999999px";
                    textArea.style.top = "-999999px";
                    document.body.appendChild(textArea);
                    textArea.focus();
                    textArea.select();
                    try {
                        document.execCommand('copy');
                        alert("URL copied to clipboard");
                    } catch (err) {
                        alert("Failed to copy");
                    }
                    textArea.remove();
                }
            }
        });
    });
};
window.showStreamGeneratorPopup = showStreamGeneratorPopup;