import { Component, OnInit } from '@angular/core';
import { ChatDocService } from 'src/services/chat.service';

@Component({
  selector: 'app-upload-document',
  templateUrl: './upload-document.component.html',
  styleUrls: ['./upload-document.component.scss']
})
export class UploadDocumentComponent implements OnInit {

  requestType: 'chat' | 'summarize' | 'ask' | 'image' | 'rag' = 'chat'; // Default to Chat
  userInput: string = '';
  imagePrompt: string = '';
  documentContent: string = '';
  aiResponse: string = '';
  selectedFile: File | null = null;
  requestOptions = [
    { label: 'Chat', value: 'chat' },
    { label: 'Summarization', value: 'summarize' },
    { label: 'Q&A', value: 'ask' },
    { label: 'Image Generation', value: 'image' }, // New option
    { label: 'Rag', value: 'rag' }
  ];
  formattedResponse: any;

  constructor(private chatDocService: ChatDocService) { }

  ngOnInit(): void {
  }


  // Handle file selection
  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    if (input.files && input.files.length > 0) {
      this.selectedFile = input.files[0];
    }
  }

  sendRequest() {
    if (this.requestType === 'chat' || this.requestType === 'rag') {
      // For chat, just send text
      this.chatDocService.sendTextRequest(this.requestType, this.userInput).subscribe({
        next: (response) => {
          this.aiResponse = response.response;
          console.error('Before:', this.aiResponse);
          this.formattedResponse = this.nl2br(this.aiResponse);
          console.error('After:', this.formattedResponse);
        },
        error: (error) => {
          console.error('Error:', error);
          this.aiResponse = 'Error processing request.';
        },
      });
    } else if (this.selectedFile) {
      // For file-based requests, send the file
      this.chatDocService.sendFileRequest(this.requestType, this.selectedFile).subscribe({
        next: (response) => {
          this.aiResponse = response.response;
          console.error('Before:', this.aiResponse);
          this.formattedResponse = this.nl2br(this.aiResponse);
          console.error('After:', this.formattedResponse);
        },
        error: (error) => {
          console.error('Error:', error);
          this.aiResponse = 'Error processing request.';
        },
      });
    } else if (this.requestType === 'image') {
      this.chatDocService.sendImageRequest(this.imagePrompt).subscribe({
        next: (response) => this.aiResponse = response.imageUrl, // Store image URL
        error: (error) => console.error('Error:', error)
      });
    }

  }

  nl2br(text: string): string {
    if (!text) return '';
  
    let newText = text
      .replace(/^"(.*)"$/, '$1') // Remove extra quotes at the start and end
      .replace(/\\n/g, '\n')      // Convert escaped `\n` to real newlines
      .replace(/\n{2,}/g, '<br><br>') // Replace double newlines with `<br><br>`
      .replace(/\n/g, '<br>');     // Replace single newlines with `<br>`

      return newText;
  }


  nl2br1(text: string): string {
    if (!text) return '';
    let cleanedText = text.replace(/^"|"$/g, ''); // Remove leading and trailing quotes

    let replacedNewlines = cleanedText.replace(/\n\n/g, '<br><br>');
    replacedNewlines = replacedNewlines.replace(/\n/g, '<br>');

    let newText = cleanedText
        .replace(/\n{2,}/g, '<br><br>')
        .replace(/\n/g, '<br>')
        .replace(/\\u(\d{4})/g, (match, grp) => String.fromCharCode(parseInt(grp, 16)));
    return newText;
  }

}
