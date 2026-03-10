var showStreamGeneratorPopup = function(itemId, serverId) {
    const apiClient = window.ApiClient;

    /* Fetch media item to know its streams */
    apiClient.getItem(apiClient.getCurrentUserId(), itemId).then((item) => {
        if (!item || !item.MediaSources || item.MediaSources.length === 0) {
            alert("Cannot get media sources for this item.");
            return;
        }

        const mediaSource = item.MediaSources[0]; /* simplify: assume first media source */

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
        const checkboxLabelStyle = 'display: flex; align-items: center; cursor: pointer;';

        html += '<label>Video Codecs</label>';
        html += '<div style="' + checkboxContainerStyle + '">';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="videoCodec" value="h264" checked style="margin-right: 5px;">H264</label>';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="videoCodec" value="hevc" checked style="margin-right: 5px;">HEVC</label>';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="videoCodec" value="av1" checked style="margin-right: 5px;">AV1</label>';
        html += '</div>';

        html += '<label>Audio Codecs</label>';
        html += '<div style="' + checkboxContainerStyle + '">';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="audioCodec" value="aac" checked style="margin-right: 5px;">AAC</label>';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="audioCodec" value="ac3" checked style="margin-right: 5px;">AC3</label>';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="audioCodec" value="eac3" checked style="margin-right: 5px;">EAC3</label>';
        html += '<label style="' + checkboxLabelStyle + '"><input type="checkbox" name="audioCodec" value="mp3" checked style="margin-right: 5px;">MP3</label>';
        html += '</div>';

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

        html += '<label style="display: flex; align-items: center; margin-bottom: 15px; cursor: pointer;">';
        html += '<input type="checkbox" id="copyTimestamps" style="margin-right: 8px;" />';
        html += '<span>Copy Timestamps</span>';
        html += '</label>';

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
        const closePopup = function() {
            if (document.body.contains(overlay)) {
                document.body.removeChild(overlay);
            }
        };

        modal.querySelector('#btnCancel').addEventListener('click', closePopup);

        /* Close on overlay click */
        overlay.addEventListener('click', function(e) {
            if (e.target === overlay) closePopup();
        });

        /* Generate URL Logic */
        modal.querySelector('#streamGeneratorForm').addEventListener('submit', function (e) {
            e.preventDefault();

            const allVideoCheckboxes = Array.from(modal.querySelectorAll('input[name="videoCodec"]'));
            const allAudioCheckboxes = Array.from(modal.querySelectorAll('input[name="audioCodec"]'));

            const videoCodecCheckboxes = allVideoCheckboxes.filter(cb => cb.checked).map(cb => cb.value);
            const audioCodecCheckboxes = allAudioCheckboxes.filter(cb => cb.checked).map(cb => cb.value);

            const allVideoSelected = videoCodecCheckboxes.length === allVideoCheckboxes.length;
            const allAudioSelected = audioCodecCheckboxes.length === allAudioCheckboxes.length;

            const videoCodecsStr = videoCodecCheckboxes.join(',');
            const audioCodecsStr = audioCodecCheckboxes.join(',');

            const audioStreamIndex = modal.querySelector('#audioStreamIndex').value;
            const subtitleStreamIndex = modal.querySelector('#subtitleStreamIndex').value;
            const subtitleMethod = modal.querySelector('#subtitleMethod').value;
            const copyTimestamps = modal.querySelector('#copyTimestamps').checked;

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
                copyTimestamps: copyTimestamps
            });

            if (videoCodecsStr && !allVideoSelected) queryParams.append('videoCodec', videoCodecsStr);
            if (audioCodecsStr && !allAudioSelected) queryParams.append('audioCodec', audioCodecsStr);
            if (audioStreamIndex !== '') queryParams.append('audioStreamIndex', audioStreamIndex);
            if (subtitleStreamIndex !== '') {
                queryParams.append('subtitleStreamIndex', subtitleStreamIndex);
                queryParams.append('subtitleMethod', subtitleMethod);
            }

            const finalUrl = serverUrl + '/Videos/' + itemId + '/master.m3u8?' + decodeURIComponent(queryParams.toString());
            modal.querySelector('#txtOutputUrl').value = finalUrl;
        });

        /* Copy logic */
        modal.querySelector('#btnCopyUrl').addEventListener('click', function() {
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