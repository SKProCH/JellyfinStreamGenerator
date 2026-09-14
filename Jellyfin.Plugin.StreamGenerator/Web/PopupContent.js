var showStreamGeneratorPopup = function (itemId, serverId) {
    const apiClient = window.ApiClient;

    const showToast = function (message) {
        const toast = document.createElement('div');
        toast.textContent = message;
        toast.style.position = 'fixed';
        toast.style.bottom = '20px';
        toast.style.left = '50%';
        toast.style.transform = 'translateX(-50%)';
        toast.style.backgroundColor = '#333';
        toast.style.color = '#fff';
        toast.style.padding = '10px 20px';
        toast.style.borderRadius = '20px';
        toast.style.zIndex = '100000';
        toast.style.fontSize = '14px';
        toast.style.boxShadow = '0 2px 10px rgba(0,0,0,0.5)';
        document.body.appendChild(toast);
        setTimeout(() => {
            toast.style.transition = 'opacity 0.5s';
            toast.style.opacity = '0';
            setTimeout(() => {
                if (document.body.contains(toast)) {
                    document.body.removeChild(toast);
                }
            }, 500);
        }, 2500);
    };

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
        })),
        apiClient.getJSON(apiClient.getUrl('StreamGenerator/Settings')).catch(() => ({})),
        apiClient.getJSON(apiClient.getUrl('Users/' + apiClient.getCurrentUserId())).catch(() => ({}))
    ]).then(([item, encodingOptions, settings, user]) => {
        if (!item || !item.MediaSources || item.MediaSources.length === 0) {
            showToast("Cannot get media sources for this item.");
            return;
        }

        const mediaSource = item.MediaSources[0]; /* simplify: assume first media source */

        const checkboxLabelStyle = 'display: flex; align-items: center; cursor: pointer;';

        /* Filter Video Codecs */
        const sourceVideoCodecs = (mediaSource.MediaStreams || []).filter(s => s.Type === 'Video').map(s => s.Codec).filter(Boolean);
        const videoCodecs = getFilteredCodecs(sourceVideoCodecs, encodingOptions.TranscodingVideoCodecs || [], ['h264', 'hevc', 'av1', 'vp9'], 'h264');
        const videoCodecsHtml = generateCodecCheckboxesHtml(videoCodecs, 'videoCodec', checkboxLabelStyle);

        const videoStream = (mediaSource.MediaStreams || []).find(s => s.Type === 'Video');
        const hasVideoBitrate = Number.isFinite(videoStream?.BitRate) && videoStream.BitRate > 0;
        const maxBitrate = hasVideoBitrate ? videoStream.BitRate : 140000000;
        const formatBitrate = function (bitrate) {
            if (bitrate >= 1000000) {
                const mbps = bitrate / 1000000;
                return mbps.toFixed(mbps % 1 === 0 ? 0 : 2) + ' Mbps';
            }

            return Math.round(bitrate / 1000) + ' Kbps';
        };

        /* Prepare Audio and Subtitle Options */
        let audioOptions = '<option value="">Default</option>';
        let subtitleOptions = '';
        const subtitleStreams = [];

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

                    subtitleStreams.push({
                        stream: stream,
                        label: name + codecStr + typeStr + forcedStr + defaultStr
                    });
                }
            });
        }

        const userConfiguration = user.Configuration || {};
        const preferredSubtitleLanguages = (userConfiguration.SubtitleLanguagePreference || '')
            .split(',')
            .map(language => language.trim().toLowerCase())
            .filter(Boolean);
        const forcedSubtitleStreams = subtitleStreams.filter(entry => entry.stream.IsForced);
        const languageMatches = function (stream, language) {
            return Boolean(language && stream.Language && stream.Language.toLowerCase() === language);
        };
        const findAutoSubtitle = function (audioStreamIndex) {
            const selectedAudioStream = (mediaSource.MediaStreams || []).find(stream =>
                stream.Type === 'Audio' && stream.Index === audioStreamIndex
            ) || (mediaSource.MediaStreams || []).find(stream => stream.Type === 'Audio');
            const selectedAudioLanguage = selectedAudioStream?.Language?.toLowerCase();

            return forcedSubtitleStreams
                .slice()
                .sort((left, right) => {
                    const leftStream = left.stream;
                    const rightStream = right.stream;
                    const leftAudioLanguage = languageMatches(leftStream, selectedAudioLanguage) ? 1 : 0;
                    const rightAudioLanguage = languageMatches(rightStream, selectedAudioLanguage) ? 1 : 0;
                    if (leftAudioLanguage !== rightAudioLanguage) return rightAudioLanguage - leftAudioLanguage;

                    const leftPreferredLanguage = preferredSubtitleLanguages.some(language => languageMatches(leftStream, language)) ? 1 : 0;
                    const rightPreferredLanguage = preferredSubtitleLanguages.some(language => languageMatches(rightStream, language)) ? 1 : 0;
                    if (leftPreferredLanguage !== rightPreferredLanguage) return rightPreferredLanguage - leftPreferredLanguage;
                    if (leftStream.IsDefault !== rightStream.IsDefault) return rightStream.IsDefault ? 1 : -1;
                    if (leftStream.IsExternal !== rightStream.IsExternal) return rightStream.IsExternal ? 1 : -1;
                    return leftStream.Index - rightStream.Index;
                })[0];
        };

        const getAutoLabel = function (autoSubtitle) {
            return autoSubtitle
                ? 'Auto (' + autoSubtitle.label + (forcedSubtitleStreams.length > 1 ? ', 1 of ' + forcedSubtitleStreams.length : '') + ')'
                : 'Auto (No subtitles)';
        };
        let autoSubtitle = findAutoSubtitle(mediaSource.DefaultAudioStreamIndex);
        let autoSubtitleStreamIndex = autoSubtitle ? autoSubtitle.stream.Index : null;
        const defaultSubtitleMethod = autoSubtitle ? 'Encode' : 'Hls';
        subtitleOptions += '<option value="auto" selected>' + getAutoLabel(autoSubtitle) + '</option>';
        subtitleOptions += '<option value="-1">None</option>';
        subtitleStreams.forEach(entry => {
            subtitleOptions += '<option value="' + entry.stream.Index + '">' + entry.label + '</option>';
        });

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
        html += '<div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px;">';
        html += '<h2 style="margin: 0; font-weight: normal;">Generate Stream URL</h2>';
        html += '<button type="button" id="btnMaxCompatibility" style="padding: 4px 10px; border: none; border-radius: 4px; cursor: pointer; font-weight: bold; background: #aa5500; color: #fff; font-size: 0.8em;">Max Compatibility</button>';
        html += '</div>';

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

        const minBitrate = 1000000;
        const bitrateStep = Math.max(100000, Math.ceil(maxBitrate / 100 / 100000) * 100000);
        const maxSelectableBitrate = Math.max(minBitrate, Math.ceil(maxBitrate / bitrateStep) * bitrateStep);
        const sliderMax = maxSelectableBitrate + bitrateStep;
        const keepOriginalLabel = hasVideoBitrate ? 'Keep original (' + formatBitrate(videoStream.BitRate) + ')' : 'Keep original';

        html += '<label>Audio Stream<br>';
        html += '<select id="audioStreamIndex" style="' + selectStyle + '">';
        html += audioOptions;
        html += '</select></label>';

        html += '<label>Subtitle Stream<br>';
        html += '<select id="subtitleStreamIndex" style="' + selectStyle + '">';
        html += subtitleOptions;
        html += '</select></label>';

        /* Build Duration Options based on Settings */
        let durationOptionsHtml = '';
        const presetHours = [1, 6, 12, 24, 72, 168, 360, 720]; // up to 30 days
        const presetLabels = {
            1: '1 Hour',
            6: '6 Hours',
            12: '12 Hours',
            24: '1 Day',
            72: '3 Days',
            168: '7 Days',
            360: '15 Days',
            720: '30 Days'
        };

        const maxDur = settings.MaxTokenDurationHours;
        const defDur = settings.DefaultTokenDurationHours;

        let validPresets = presetHours;
        if (maxDur !== undefined && maxDur !== null) {
            validPresets = validPresets.filter(h => h <= maxDur);
            if (!validPresets.includes(maxDur)) {
                validPresets.push(maxDur);
                presetLabels[maxDur] = maxDur + ' Hours (Max)';
            }
        }

        if (defDur !== undefined && defDur !== null && !validPresets.includes(defDur)) {
            if (maxDur === null || maxDur === undefined || defDur <= maxDur) {
                validPresets.push(defDur);
                presetLabels[defDur] = defDur + ' Hours (Default)';
            }
        }

        validPresets.sort((a, b) => a - b);

        validPresets.forEach(h => {
            const isSelected = h === defDur ? ' selected' : '';
            durationOptionsHtml += '<option value="' + h + '"' + isSelected + '>' + (presetLabels[h] || h + ' Hours') + '</option>';
        });

        if (maxDur === null || maxDur === undefined) {
            durationOptionsHtml += '<option value=""' + (defDur === null || defDur === undefined ? ' selected' : '') + '>Infinite</option>';
        }

        html += '<label>Subtitle Method<br>';
        html += '<select id="subtitleMethod" style="' + selectStyle + '">';
        html += '<option value="Hls"' + (defaultSubtitleMethod === 'Hls' ? ' selected' : '') + '>HLS</option>';
        html += '<option value="Encode"' + (defaultSubtitleMethod === 'Encode' ? ' selected' : '') + '>Burn In (Encode)</option>';
        html += '<option value="Embed">Embed</option>';
        html += '<option value="Drop">Drop</option>';
        html += '</select></label>';

        html += '<details style="margin-bottom: 15px; background: #333; padding: 10px; border-radius: 4px; border: 1px solid #444;">';
        html += '<summary style="cursor: pointer; font-weight: bold; margin-bottom: 5px;">Advanced</summary>';

        html += '<label style="display: block; margin-top: 10px;">Token Lifetime:<br>';
        html += '<select id="tokenDurationHours" style="' + selectStyle + ' margin-bottom: 0;">';
        html += durationOptionsHtml;
        html += '</select></label>';

        html += '<label style="display: block; margin-top: 15px;">Max Video Bitrate: <span id="bitrateDisplay">' + keepOriginalLabel + '</span><br>';
        html += '<div style="display: flex; align-items: center; gap: 8px;">';
        html += '<input type="range" id="maxVideoBitrate" style="' + selectStyle + ' cursor: pointer; margin-bottom: 0; flex: 1;" min="' + minBitrate + '" max="' + sliderMax + '" step="' + bitrateStep + '" value="' + sliderMax + '">';
        html += '</div></label>';

        html += '<label style="display: block; margin-top: 15px;">Segment Container:<br>';
        html += '<select id="segmentContainer" style="' + selectStyle + ' margin-bottom: 0;">';
        html += '<option value="mp4" selected>fMP4 (recommended)</option>';
        html += '<option value="ts">MPEG-TS</option>';
        html += '</select>';
        html += '<small style="display: block; margin-top: 6px; color: #bbb; line-height: 1.4;">';
        html += 'Choose MPEG-TS for older, embedded, or otherwise limited HLS players that cannot play fragmented MP4.';
        html += '</small></label>';

        html += '<label style="display: flex; align-items: center; margin-top: 15px; cursor: pointer;">';
        html += '<input type="checkbox" id="copyTimestamps" style="margin-right: 8px;" checked />';
        html += '<span>Copy Timestamps</span>';
        html += '</label>';

        if (settings.GenerateCustomApiTokens) {
            html += '<label style="display: flex; align-items: center; margin-top: 15px; cursor: pointer;">';
            html += '<input type="checkbox" id="rememberPlaybackProgress" style="margin-right: 8px;"' + (settings.RememberPlaybackProgressByDefault !== false ? ' checked' : '') + ' />';
            html += '<span>Remember playback progress</span>';
            html += '</label>';
        }
        html += '</details>';

        const btnStyle = 'padding: 8px 16px; border: none; border-radius: 4px; cursor: pointer; font-weight: bold; margin-left: 10px;';

        html += '<div style="display: flex; justify-content: flex-end; margin-top: 20px;">';
        html += '<button type="button" id="btnCancel" style="' + btnStyle + ' background: #444; color: #fff;">Close</button>';
        html += '<button type="submit" style="' + btnStyle + ' background: #52b54b; color: #fff;">Generate and Copy</button>';
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

        /* Maximize Compatibility: keep only h264 + aac */
        modal.querySelector('#btnMaxCompatibility').addEventListener('click', function () {
            modal.querySelectorAll('input[name="videoCodec"]').forEach(function (cb) {
                cb.checked = cb.value === 'h264';
            });
            modal.querySelectorAll('input[name="audioCodec"]').forEach(function (cb) {
                cb.checked = cb.value === 'aac';
            });
        });

        /* Handle Bitrate Slider Update */
        modal.querySelector('#maxVideoBitrate').addEventListener('input', function (e) {
            const value = parseInt(e.target.value, 10);
            modal.querySelector('#bitrateDisplay').textContent = value === sliderMax
                ? keepOriginalLabel
                : formatBitrate(value);
        });

        modal.querySelector('#audioStreamIndex').addEventListener('change', function (e) {
            const selectedAudioIndex = e.target.value === ''
                ? mediaSource.DefaultAudioStreamIndex
                : Number.parseInt(e.target.value, 10);
            autoSubtitle = findAutoSubtitle(selectedAudioIndex);
            autoSubtitleStreamIndex = autoSubtitle ? autoSubtitle.stream.Index : null;
            modal.querySelector('#subtitleStreamIndex option[value="auto"]').textContent = getAutoLabel(autoSubtitle);

            if (modal.querySelector('#subtitleStreamIndex').value === 'auto') {
                modal.querySelector('#subtitleMethod').value = autoSubtitle ? 'Encode' : 'Drop';
            }
        });

        modal.querySelector('#subtitleStreamIndex').addEventListener('change', function (e) {
            if (e.target.value === 'auto') {
                modal.querySelector('#subtitleMethod').value = autoSubtitleStreamIndex === null ? 'Drop' : 'Encode';
            } else if (e.target.value === '-1') {
                modal.querySelector('#subtitleMethod').value = 'Drop';
            }
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
            const subtitleSelection = modal.querySelector('#subtitleStreamIndex').value;
            const subtitleMethod = modal.querySelector('#subtitleMethod').value;
            const copyTimestamps = modal.querySelector('#copyTimestamps').checked;
            const selectedBitrate = parseInt(modal.querySelector('#maxVideoBitrate').value, 10);
            const maxVideoBitrate = selectedBitrate === sliderMax ? null : selectedBitrate;
            const segmentContainer = modal.querySelector('#segmentContainer').value;

            const serverUrl = apiClient.serverAddress();

            const buildUrl = function (apiKey) {
                const queryParams = new URLSearchParams({
                    deviceId: 'stream_generator',
                    playSessionId: 'stream_generator_random',
                    api_key: apiKey,
                    mediaSourceId: mediaSource.Id,
                    static: false,
                    enableAutoStreamCopy: true,
                    allowVideoStreamCopy: true,
                    allowAudioStreamCopy: true,
                    copyTimestamps: copyTimestamps,
                    segmentContainer: segmentContainer
                });

                 if (maxVideoBitrate !== null) queryParams.append('videoBitrate', maxVideoBitrate);
                if (videoCodecsStr) queryParams.append('videoCodec', videoCodecsStr);
                if (audioCodecsStr) queryParams.append('audioCodec', audioCodecsStr);
                if (audioStreamIndex !== '') queryParams.append('audioStreamIndex', audioStreamIndex);
                const effectiveSubtitleStreamIndex = subtitleSelection === 'auto'
                    ? autoSubtitleStreamIndex
                    : subtitleSelection === '-1' ? -1 : subtitleSelection;
                const effectiveSubtitleMethod = subtitleSelection === 'auto'
                    ? autoSubtitleStreamIndex === null ? 'Drop' : subtitleMethod
                    : subtitleSelection === '-1' ? 'Drop' : subtitleMethod;

                if (effectiveSubtitleStreamIndex !== null) {
                    queryParams.append('subtitleStreamIndex', effectiveSubtitleStreamIndex);
                    queryParams.append('subtitleMethod', effectiveSubtitleMethod);
                }

                const finalUrl = serverUrl + '/Videos/' + itemId + '/master.m3u8?' + decodeURIComponent(queryParams.toString());

                if (navigator.clipboard && window.isSecureContext) {
                    navigator.clipboard.writeText(finalUrl).then(() => {
                        showToast("URL copied to clipboard");
                    }).catch(err => {
                        showToast("Failed to copy: " + err);
                    });
                } else {
                    const textArea = document.createElement("textarea");
                    textArea.value = finalUrl;
                    textArea.style.position = "fixed";
                    textArea.style.left = "-999999px";
                    textArea.style.top = "-999999px";
                    document.body.appendChild(textArea);
                    textArea.focus();
                    textArea.select();
                    try {
                        document.execCommand('copy');
                        showToast("URL copied to clipboard");
                    } catch (err) {
                        showToast("Failed to copy");
                    }
                    textArea.remove();
                }
            };

            if (settings.GenerateCustomApiTokens) {
                const selectedDuration = modal.querySelector('#tokenDurationHours').value;
                const progressCheckbox = modal.querySelector('#rememberPlaybackProgress');
                const queryObj = {
                    itemId: itemId,
                    rememberPlaybackProgress: progressCheckbox ? progressCheckbox.checked : settings.RememberPlaybackProgressByDefault !== false
                };
                if (selectedDuration !== '') {
                    queryObj.durationHours = selectedDuration;
                }

                apiClient.fetch({
                    type: 'POST',
                    url: apiClient.getUrl('StreamGenerator/GenerateToken', queryObj),
                    dataType: 'text'
                }).then(function (token) {
                    // ASP.NET Core returns strings as JSON strings (with quotes), so we must strip them
                    const cleanToken = token.replace(/^"|"$/g, '');
                    buildUrl(cleanToken);
                }).catch(function () {
                    buildUrl(apiClient.accessToken());
                });
            } else {
                buildUrl(apiClient.accessToken());
            }
        });
    });
};
window.showStreamGeneratorPopup = showStreamGeneratorPopup;
